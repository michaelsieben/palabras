using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// Plan §3.1-§3.3: one transaction per batch, attempts ordered by (card_id, reviewed_at),
// per-item outcome instead of a batch-level conflict. FSRS is not invertible, so a late
// offline attempt is recorded for audit but never rewinds card state.
internal static class LearnAttempts
{
    private const int MaximumDurationMs = 600_000;

    internal sealed record NormalizedAttempt(
        AttemptRequest Request, string ClientAttemptId, string Mode, Rating Rating,
        DateTimeOffset ReviewedAt, long ReviewedAtMs, long? DurationMs);

    internal static IReadOnlyList<NormalizedAttempt> Normalize(IReadOnlyList<AttemptRequest?> attempts)
    {
        var normalized = new List<NormalizedAttempt>(attempts.Count);
        foreach (var attempt in attempts)
        {
            if (attempt is null || attempt.ReviewedAtUtc is not { } reviewedAt
                || !Guid.TryParse(attempt.ClientAttemptId, out var clientId)
                || attempt.CardId <= 0 || attempt.CardEpoch < 0
                || attempt.Rating is < 1 or > 4 || !AttemptModes.IsKnown(attempt.Mode))
            {
                throw new ArgumentException("Attempt batch contains an unusable item.");
            }
            // Canonical form so a replay with different casing still dedupes; the response
            // echoes the client's original string so the offline queue can match it.
            normalized.Add(new NormalizedAttempt(attempt, clientId.ToString("D"), attempt.Mode!,
                (Rating)attempt.Rating, reviewedAt,
                reviewedAt.ToUnixTimeMilliseconds(),
                attempt.DurationMs is { } duration ? Math.Clamp(duration, 0, MaximumDurationMs) : null));
        }
        return normalized;
    }

    internal static async Task<IReadOnlyList<AttemptResult>> ApplyAsync(PalabrasDbContext database, int userId,
        LearnerClock clock, UserSettings settings, IReadOnlyList<NormalizedAttempt> attempts,
        DateTimeOffset receivedAt, IScheduler scheduler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(scheduler);

        var options = LearnSettings.ToSchedulerOptions(settings);
        var receivedAtMs = receivedAt.ToUnixTimeMilliseconds();
        // Plan §3.2: apply order is (card_id, reviewed_at). Clamping is monotonic, so ordering
        // by the client instant and by the clamped instant agree per card.
        var ordered = attempts.OrderBy(attempt => attempt.Request.CardId)
            .ThenBy(attempt => attempt.ReviewedAtMs).ToArray();

        // Plan §3.2 outcome precedence must be decided against CURRENT rows. SQLite serialises
        // writers, not reads, and EF's default SQLite transaction is deferred: with `BEGIN` the
        // first read below would still be taken before any write lock, so a second request or the
        // CLI could commit in between and two flushes would both apply (reps = 1 twice, one
        // memory surviving) or a duplicate id would lose to the UNIQUE index and surface as 500.
        // Take the write lock before the first read: `BEGIN IMMEDIATE` (deferred: false) acquires
        // SQLite's RESERVED lock up front, so every read and the save below run on one snapshot no
        // other writer can move, and the batch stays one transaction. WAL keeps readers
        // (plan/forecast) concurrent. A competing writer now waits (busy timeout, 30 s here)
        // instead of being silently overwritten.
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)database.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await database.Database.UseTransactionAsync(transaction, cancellationToken);

        var cardIds = ordered.Select(attempt => attempt.Request.CardId).Distinct().ToArray();
        var cards = await database.Cards.Where(card => card.UserId == userId && cardIds.Contains(card.Id))
            .ToDictionaryAsync(card => card.Id, cancellationToken);
        var ownership = await (from card in database.Cards.AsNoTracking()
                               join sense in database.Senses on card.SenseId equals sense.Id
                               join word in database.Words on sense.WordId equals word.Id
                               where card.UserId == userId && cardIds.Contains(card.Id)
                               select new { card.Id, sense.WordId, word.Status })
            .ToDictionaryAsync(row => row.Id, cancellationToken);
        var attemptIds = ordered.Select(attempt => attempt.ClientAttemptId).ToArray();
        var seen = (await database.ReviewLogs.AsNoTracking()
                .Where(log => attemptIds.Contains(log.ClientAttemptId))
                .Select(log => log.ClientAttemptId).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        // Only the words touched by this batch: a retained removed sense must reject its cards
        // even when an earlier unsuspend left suspended_reason null (S6b review observation 1).
        var active = await ActiveSenses.LoadAsync(database,
            ownership.Values.Select(row => row.WordId).Distinct().ToArray(), cancellationToken);

        var results = new List<AttemptResult>(ordered.Length);
        foreach (var attempt in ordered)
        {
            var echoed = attempt.Request.ClientAttemptId!;
            if (!seen.Add(attempt.ClientAttemptId))
            {
                results.Add(new AttemptResult(echoed, AttemptStatus.Duplicate, null));
                continue;
            }
            if (!cards.TryGetValue(attempt.Request.CardId, out var card) || !ownership.TryGetValue(card.Id, out var owner))
            {
                // Unknown and not-owned ids take the same branch, so a caller cannot learn whether
                // someone else's card exists (plan §4: only the single-card routes answer 404, and
                // only for an id the caller put in the URL). The closed status enum has no
                // "unavailable" value; §3.2's `suspended` means "not reviewable", which is exactly
                // what an unreachable card is, and the item is never applied either way.
                results.Add(new AttemptResult(echoed, AttemptStatus.Suspended, null));
                continue;
            }

            var reviewedAt = clock.ClampReviewedAt(attempt.ReviewedAt);
            var reviewedAtMs = reviewedAt.ToUnixTimeMilliseconds();
            var status = Outcome(card, owner.Status, active, attempt, reviewedAtMs);
            if (status is not null)
            {
                database.ReviewLogs.Add(Rejected(card, owner.WordId, attempt, userId, reviewedAtMs,
                    receivedAtMs, settings.WeightsVersion));
                results.Add(new AttemptResult(echoed, status, null));
                continue;
            }

            var outcome = scheduler.Review(ToMemory(card), attempt.Rating, reviewedAt, options);
            var leech = Apply(card, outcome, reviewedAt, reviewedAtMs, settings.LeechLapses, clock);
            database.ReviewLogs.Add(Applied(card, owner.WordId, attempt, userId, reviewedAtMs,
                receivedAtMs, settings.WeightsVersion, outcome));
            results.Add(new AttemptResult(echoed, AttemptStatus.Applied,
                new AttemptCardResult(card.Id, (int)card.State, outcome.After.DueUtc, outcome.IntervalAfterDays, leech)));
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private static string? Outcome(Card card, string wordStatus, ActiveSenses active, NormalizedAttempt attempt,
        long reviewedAtMs)
    {
        if (card.Epoch != attempt.Request.CardEpoch)
        {
            return AttemptStatus.StaleEpoch;
        }
        if (card.SuspendedReason is not null || wordStatus != "approved" || !active.IsActive(card.SenseId))
        {
            // §3.2 "suspended": the card is suspended or not approved. A sense the content file
            // dropped is not approved content either, so its retained cards are rejected here even
            // when their own reason was cleared (S6b review observation 1).
            return AttemptStatus.Suspended;
        }
        // Compare the clamped instant: that is the value the row will carry.
        if (card.LastReviewMs is { } lastReview && reviewedAtMs < lastReview)
        {
            return AttemptStatus.OutOfOrder;
        }
        return null;
    }

    private static CardMemory ToMemory(Card card) => new(card.State, card.Step, card.Stability, card.Difficulty,
        DateTimeOffset.FromUnixTimeMilliseconds(card.DueMs),
        card.LastReviewMs is { } lastReview ? DateTimeOffset.FromUnixTimeMilliseconds(lastReview) : null);

    private static bool Apply(Card card, ReviewOutcome outcome, DateTimeOffset reviewedAt, long reviewedAtMs,
        int leechLapses, LearnerClock clock)
    {
        var leech = false;
        if (outcome.IsLapse)
        {
            card.Lapses = checked(card.Lapses + 1);
            // Plan §3.3: leech on the transition only, and never overwrite a manual pause.
            if (!card.IsLeech && card.Lapses >= leechLapses)
            {
                card.IsLeech = true;
                card.SuspendedReason ??= "leech";
                leech = true;
            }
        }
        card.State = outcome.After.State;
        card.Step = outcome.After.Step;
        card.Stability = outcome.After.Stability;
        card.Difficulty = outcome.After.Difficulty;
        card.DueMs = outcome.After.DueUtc.ToUnixTimeMilliseconds();
        card.LastReviewMs = reviewedAtMs;
        card.Reps = checked(card.Reps + 1);
        // Plan §2.3: the quota is consumed by the first applied review, not by a plan fetch.
        card.IntroducedLocalDate ??= clock.LocalDateOf(reviewedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return leech;
    }

    private static ReviewLog Applied(Card card, int wordId, NormalizedAttempt attempt, int userId,
        long reviewedAtMs, long receivedAtMs, int weightsVersion, ReviewOutcome outcome) => new()
    {
        ClientAttemptId = attempt.ClientAttemptId,
        UserId = userId,
        WordId = wordId,
        CardId = card.Id,
        CardEpoch = card.Epoch,
        SessionId = attempt.Request.SessionId,
        Rating = attempt.Rating,
        ReviewedAtMs = reviewedAtMs,
        ReceivedAtMs = receivedAtMs,
        ElapsedDays = outcome.ElapsedDaysAtReview,
        IntervalBeforeDays = outcome.IntervalBeforeDays,
        IntervalAfterDays = outcome.IntervalAfterDays,
        DurationMs = attempt.DurationMs is { } duration ? (int)duration : null,
        Mode = attempt.Mode,
        IsCorrectMeaning = attempt.Request.IsCorrectMeaning,
        IsCorrectForm = attempt.Request.IsCorrectForm,
        FeedbackKind = attempt.Request.FeedbackKind,
        Overridden = attempt.Request.Overridden ?? false,
        AnswerText = attempt.Request.AnswerText,
        StateBefore = outcome.Before.State,
        StabilityBefore = outcome.Before.Stability,
        DifficultyBefore = outcome.Before.Difficulty,
        StateAfter = outcome.After.State,
        StabilityAfter = outcome.After.Stability,
        DifficultyAfter = outcome.After.Difficulty,
        DueAfterMs = outcome.After.DueUtc.ToUnixTimeMilliseconds(),
        WeightsVersion = weightsVersion,
        CountsForSrs = true
    };

    // Non-applied outcomes keep the row for audit but never move the card (counts_for_srs = 0).
    // The outcome itself is recoverable from counts_for_srs plus card_epoch vs cards.epoch.
    private static ReviewLog Rejected(Card card, int wordId, NormalizedAttempt attempt, int userId,
        long reviewedAtMs, long receivedAtMs, int weightsVersion) => new()
    {
        ClientAttemptId = attempt.ClientAttemptId,
        UserId = userId,
        WordId = wordId,
        CardId = card.Id,
        // The client's epoch is what was being reviewed; the row explains why it went stale.
        CardEpoch = attempt.Request.CardEpoch,
        SessionId = attempt.Request.SessionId,
        Rating = attempt.Rating,
        ReviewedAtMs = reviewedAtMs,
        ReceivedAtMs = receivedAtMs,
        ElapsedDays = 0,
        IntervalBeforeDays = null,
        IntervalAfterDays = 0,
        DurationMs = attempt.DurationMs is { } duration ? (int)duration : null,
        Mode = attempt.Mode,
        IsCorrectMeaning = attempt.Request.IsCorrectMeaning,
        IsCorrectForm = attempt.Request.IsCorrectForm,
        FeedbackKind = attempt.Request.FeedbackKind,
        Overridden = attempt.Request.Overridden ?? false,
        AnswerText = attempt.Request.AnswerText,
        StateBefore = card.State,
        StabilityBefore = card.Stability,
        DifficultyBefore = card.Difficulty,
        StateAfter = card.State,
        StabilityAfter = card.Stability,
        DifficultyAfter = card.Difficulty,
        DueAfterMs = card.DueMs,
        WeightsVersion = weightsVersion,
        CountsForSrs = false
    };
}
