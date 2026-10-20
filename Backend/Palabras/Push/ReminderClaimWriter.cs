using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Push;

// The frozen `push_deliveries.result` vocabulary (U1 CHECK). Kept next to the writer so the sender's
// classifier and this loop cannot drift apart; M8ReminderServiceTests asserts they still agree.
internal static class PushDeliveryResults
{
    internal const string Pending = "pending";
    internal const string Sent = "sent";
    internal const string Gone = "gone";
    internal const string Rejected = "rejected";
    internal const string Transient = "transient";
}

// One device's attempt state for a claimed day, as the send loop needs it: the budget already spent
// (`Attempts`) and the last outcome (`Result`).
internal sealed record ReminderDelivery(int Id, int? SubscriptionId, int Attempts, string Result);

// U6's caller-owned side of the reminder log (U5 review §4, handoff): ReminderLogStore takes an
// ambient PalabrasDbContext, so the claim and its push_deliveries rows are written inside ONE
// `BEGIN IMMEDIATE` transaction opened here. Two ticks (two processes, or a deploy overlapping a
// tick) therefore serialise the DECISION — the read-ahead, the insert and the delivery fan-out —
// instead of both reading "no row" and racing the UNIQUE insert, and a lost writer sees
// SQLITE_BUSY, which is retried instead of failing the tick.
//
// It does not serialise the SEND: the transaction commits before delivery, so two ticks resuming
// the same `sending` claim can both POST, and a window-boundary finaliser can record `failed`
// while the other process's 201 lands. Delivery is at-least-once within the window by design
// (plan §2.5): both pushes carry the same Topic, so the push service replaces rather than
// duplicates the display, and the receipt of a push — not the `result` column — is the truth for
// "the learner was shown a reminder".
//
// Every write goes through ReminderLogStore: this type only owns the transaction boundary, the
// delivery fan-out and the busy retry. Suppress and finalise are single statements, so they need no
// transaction of their own.
internal sealed class ReminderClaimWriter
{
    // Bounded, not a loop: the tick runs once a minute, and a write lock that outlives three attempts
    // is the next tick's problem (it resumes the same day).
    internal const int MaximumBusyAttempts = 3;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Func<PalabrasDbContext> _openDatabase;
    private readonly ILogger<ReminderClaimWriter> _logger;
    private readonly Func<int, CancellationToken, Task> _wait;

    public ReminderClaimWriter(DatabaseSettings settings, ILogger<ReminderClaimWriter> logger)
        : this(() => settings.CreateContext(), logger, WaitBeforeRetryAsync)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    // Test seam: the caller supplies how a context is opened (a zero busy timeout turns a held write
    // lock into a deterministic SQLITE_BUSY instead of a 30-second wait) and how to wait between
    // attempts (a test releases the lock there instead of sleeping).
    internal ReminderClaimWriter(Func<PalabrasDbContext> openDatabase, ILogger<ReminderClaimWriter> logger,
        Func<int, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(openDatabase);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(wait);
        _openDatabase = openDatabase;
        _logger = logger;
        _wait = wait;
    }

    // The claim: `sending` plus one `pending` delivery row per deliverable device, committed together
    // so a crash can never leave a decision without its devices (or the other way round). Delivery
    // rows are only created when THIS call inserted the decision: a resumed day already has them, and
    // a day another tick decided must not be given a second set.
    internal async Task<ReminderLogClaim> ClaimAsync(int userId, DateOnly localDate, bool skipAvailable,
        int ttlSeconds, long expiresAtMs, IReadOnlyList<int> subscriptionIds, long nowMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriptionIds);
        return await WithBusyRetryAsync(async token =>
        {
            await using var database = _openDatabase();
            await using var transaction = await PushEndpoints.PushWriteTransaction.BeginAsync(database, token);
            var claim = await ReminderLogStore.ClaimAsync(database, userId, localDate, skipAvailable,
                ttlSeconds, expiresAtMs, nowMs, token);
            if (claim.Claimed)
            {
                foreach (var subscriptionId in subscriptionIds)
                {
                    database.PushDeliveries.Add(new PushDelivery
                    {
                        ReminderLogId = claim.Row.Id,
                        SubscriptionId = subscriptionId,
                        Attempts = 0,
                        Result = PushDeliveryResults.Pending,
                        LastStatus = null,
                        UpdatedAtMs = nowMs
                    });
                }
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return claim;
        }, "the reminder claim", cancellationToken);
    }

    // A day with no send at all. Insert-only by design (the store's own contract): a day that was
    // already claimed is finalised with CompleteAsync instead.
    internal Task<bool> SuppressAsync(int userId, DateOnly localDate, string result, bool skipAvailable,
        long nowMs, CancellationToken cancellationToken) =>
        WithBusyRetryAsync(async token =>
        {
            await using var database = _openDatabase();
            var claim = await ReminderLogStore.SuppressAsync(database, userId, localDate, result,
                skipAvailable, nowMs, token);
            return claim.Claimed;
        }, "the reminder suppression", cancellationToken);

    // The one transition out of `sending`. False means nothing was recorded: the row is gone, or
    // another tick already decided it (a decided row is never rewritten).
    internal Task<bool> CompleteAsync(int reminderLogId, string result, int? ttlSeconds, long? expiresAtMs,
        long nowMs, CancellationToken cancellationToken) =>
        WithBusyRetryAsync(async token =>
        {
            await using var database = _openDatabase();
            return await ReminderLogStore.CompleteAsync(database, reminderLogId, result, ttlSeconds,
                expiresAtMs, nowMs, token);
        }, "the reminder finalisation", cancellationToken);

    internal async Task<IReadOnlyList<ReminderDelivery>> LoadDeliveriesAsync(int reminderLogId,
        CancellationToken cancellationToken)
    {
        await using var database = _openDatabase();
        return await database.PushDeliveries.AsNoTracking()
            .Where(row => row.ReminderLogId == reminderLogId)
            .OrderBy(row => row.Id)
            .Select(row => new ReminderDelivery(row.Id, row.SubscriptionId, row.Attempts, row.Result))
            .ToListAsync(cancellationToken);
    }

    private async Task<T> WithBusyRetryAsync<T>(Func<CancellationToken, Task<T>> operation, string what,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (attempt < MaximumBusyAttempts && IsBusy(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                // A concurrent writer (another tick, the settings route, a deploy) holds the single
                // SQLite write lock. Failing here would skip today's only reminder, so the claim is
                // retried; a lock that never clears surfaces after the last attempt and the next
                // minute tick resumes the same day.
                _logger.LogWarning(exception,
                    "SQLite was busy during {Operation} (attempt {Attempt} of {Maximum}); retrying.",
                    what, attempt, MaximumBusyAttempts);
                await _wait(attempt, cancellationToken);
            }
        }
    }

    private static bool IsBusy(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked,
        DbUpdateException update => update.InnerException is { } inner && IsBusy(inner),
        _ => false
    };

    private static async Task WaitBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        // 200 ms, then 400 ms: long enough to outlast another process's short write, short enough
        // that a whole tick stays well inside the minute.
        await Task.Delay(FirstRetryDelay * (1 << (attempt - 1)), cancellationToken);
    }
}
