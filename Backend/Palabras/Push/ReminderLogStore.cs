using Microsoft.EntityFrameworkCore;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Push;

// One decision row, created or already present. `Claimed` says which of the two this call is: a
// second caller that loses the UNIQUE key gets the winner's row back instead of an exception.
internal sealed record ReminderLogClaim(ReminderLog Row, bool Claimed);

// M8 reminder decision log (plan §2.3/§2.4, design-amendment.md ΔF :642-647). The frozen schema
// allows exactly one row per learner, local day, kind and channel, and that UNIQUE key is the
// once-per-day claim across restarts, ticks and crashes — so this type never checks "did we send
// already?" in memory, it asks the database by attempting the write.
//
// Three outcomes the later sender (U6) needs and nothing else:
// - planned:     ClaimAsync  -> result='sending' (a resumable send owns today)
// - suppressed:  SuppressAsync -> result='window_missed' | 'no_target' (a decision, but no send)
// - finished:    CompleteAsync -> result='sent' | 'failed' | 'gone'
// A day that was done, skipped, muted or disabled deliberately writes NO row (plan §2.4), so an
// absent row means "there was no decision", not "the decision was suppressed".
//
// The CHECK value sets are reproduced here as the writer's own contract: a typo becomes an
// ArgumentOutOfRangeException at the call site instead of a SqliteException from a missing row.
internal static class ReminderLogStore
{
    internal const string DailyKind = "daily";
    internal const string WebPushChannel = "webpush";

    internal const string SendingResult = "sending";
    internal const string SentResult = "sent";
    internal const string FailedResult = "failed";
    internal const string GoneResult = "gone";
    internal const string WindowMissedResult = "window_missed";
    internal const string NoTargetResult = "no_target";

    private static readonly string[] TerminalResults = [SentResult, FailedResult, GoneResult];
    private static readonly string[] SuppressedResults = [WindowMissedResult, NoTargetResult];

    // The learner-day key the whole feature agrees on (yyyy-MM-dd via LearnDayStatus.Key), so the
    // log, day_status and the notification payload can never disagree about "today".
    internal static string Key(DateOnly localDate) => LearnDayStatus.Key(localDate);

    internal static bool IsTerminalResult(string result) => Array.IndexOf(TerminalResults, result) >= 0;

    internal static bool IsSuppressedResult(string result) => Array.IndexOf(SuppressedResults, result) >= 0;

    // Today's decision for this learner, or null when nothing was decided yet — the reader behind
    // "did we remind this learner today, and why not".
    internal static async Task<ReminderLog?> FindAsync(PalabrasDbContext database, int userId, DateOnly localDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var key = Key(localDate);
        return await database.ReminderLogs.AsNoTracking().SingleOrDefaultAsync(
            row => row.UserId == userId && row.LocalDate == key && row.Kind == DailyKind
                && row.Channel == WebPushChannel,
            cancellationToken);
    }

    // The claim: insert `sending` for today. A row that already exists is returned unchanged, so a
    // minute tick after a restart resumes today's decision instead of creating a second one.
    internal static async Task<ReminderLogClaim> ClaimAsync(PalabrasDbContext database, int userId,
        DateOnly localDate, bool skipAvailable, int ttlSeconds, long expiresAtMs, long nowMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var key = Key(localDate);
        var existing = await database.ReminderLogs.AsNoTracking().SingleOrDefaultAsync(
            row => row.UserId == userId && row.LocalDate == key && row.Kind == DailyKind
                && row.Channel == WebPushChannel,
            cancellationToken);
        if (existing is not null)
        {
            return new ReminderLogClaim(existing, false);
        }
        var row = new ReminderLog
        {
            UserId = userId,
            LocalDate = key,
            Kind = DailyKind,
            Channel = WebPushChannel,
            Result = SendingResult,
            TtlS = ttlSeconds,
            ExpiresAtMs = expiresAtMs,
            SkipAvailable = skipAvailable,
            CreatedAtMs = nowMs,
            UpdatedAtMs = nowMs
        };
        return await InsertOrReadAsync(database, row, cancellationToken);
    }

    // A decision that produced no send at all (the window had already closed, or every device was
    // undeliverable). Terminal on insert: a later tick reads the row back and stops reconsidering
    // the day, which is what makes the outcome honest after downtime.
    internal static async Task<ReminderLogClaim> SuppressAsync(PalabrasDbContext database, int userId,
        DateOnly localDate, string result, bool skipAvailable, long nowMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!IsSuppressedResult(result))
        {
            throw new ArgumentOutOfRangeException(nameof(result), result,
                "Only window_missed and no_target are suppressed reminder results.");
        }
        var key = Key(localDate);
        var row = new ReminderLog
        {
            UserId = userId,
            LocalDate = key,
            Kind = DailyKind,
            Channel = WebPushChannel,
            Result = result,
            TtlS = null,
            ExpiresAtMs = null,
            SkipAvailable = skipAvailable,
            CreatedAtMs = nowMs,
            UpdatedAtMs = nowMs
        };
        return await InsertOrReadAsync(database, row, cancellationToken);
    }

    // Finalises a claimed row. `ttl_s` and `expires_at_ms` are replaced with the last attempt's
    // values (plan §2.4 recomputes them per attempt), and `updated_at_ms` is the completion instant.
    // Returns false when the row no longer exists, so a caller cannot believe it recorded anything.
    internal static async Task<bool> CompleteAsync(PalabrasDbContext database, int reminderLogId, string result,
        int? ttlSeconds, long? expiresAtMs, long nowMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!IsTerminalResult(result))
        {
            throw new ArgumentOutOfRangeException(nameof(result), result,
                "Only sent, failed and gone are final reminder results.");
        }
        var row = await database.ReminderLogs.SingleOrDefaultAsync(
            candidate => candidate.Id == reminderLogId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        row.Result = result;
        if (ttlSeconds is { } ttl)
        {
            row.TtlS = ttl;
        }
        if (expiresAtMs is { } expires)
        {
            row.ExpiresAtMs = expires;
        }
        row.UpdatedAtMs = nowMs;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The UNIQUE (user_id, local_date, kind, channel) is the claim, so losing the insert is a
    // normal outcome, not an error: another tick (or a restart racing a tick) already owns today.
    // Any other constraint failure (FK, CHECK) has no competing row to read and is rethrown.
    private static async Task<ReminderLogClaim> InsertOrReadAsync(PalabrasDbContext database, ReminderLog row,
        CancellationToken cancellationToken)
    {
        database.ReminderLogs.Add(row);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new ReminderLogClaim(row, true);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            database.Entry(row).State = EntityState.Detached;
            var existing = await database.ReminderLogs.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.UserId == row.UserId && candidate.LocalDate == row.LocalDate
                    && candidate.Kind == row.Kind && candidate.Channel == row.Channel,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }
            return new ReminderLogClaim(existing, false);
        }
    }
}
