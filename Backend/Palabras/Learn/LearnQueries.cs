using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// Plan §2.3 queue predicates. Every predicate stays inside IQueryable so EF composes it
// into one SQL statement; test M5LearnApiTests.QueueQueryTranslatesEntirelyToSqliteSql
// fails if any part falls back to client evaluation. day boundaries come from
// LearnerClock only (plan §3), never from a second UTC->local conversion.
internal sealed record QueueBounds(int UserId, string LocalToday, long StartOfLocalDayMs, long EndOfLocalDayMs)
{
    internal static QueueBounds From(LearnerClock clock, int userId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new QueueBounds(userId,
            clock.LocalToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            clock.StartOfLocalDayUtc.ToUnixTimeMilliseconds(),
            clock.EndOfLocalDayUtc.ToUnixTimeMilliseconds());
    }
}

internal static class LearnQueries
{
    // The content schema bounds `difficulty` at 9999 (content/schema/word.schema.json), so
    // int.MaxValue is above every possible rank by construction. NULL therefore sorts after every
    // ranked word instead of landing mid-ramp.
    internal const int UnrankedDifficulty = int.MaxValue;

    internal static IQueryable<Card> Eligible(PalabrasDbContext database, QueueBounds bounds, ActiveSenses active)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(active);
        // Locals, not nested member access: EF parameterises them like the proven S6a query.
        var userId = bounds.UserId;
        var localToday = bounds.LocalToday;
        var endOfLocalDayMs = bounds.EndOfLocalDayMs;
        // S6b retention gate: a sense row the content file dropped is kept for FK integrity but is
        // not part of the corpus. Only the current version's snapshot says which ordinals are live,
        // because a manual/leech card on a removed sense keeps its non-null/none reason.
        var activeSenseIds = active.SenseIds.ToArray();
        // Bury encoding (plan §4): buried_until_local_date is the LAST local date the card stays
        // hidden, so it is visible once that date is before today. Bury stores today => hidden for
        // the rest of today and planned again tomorrow. Keep this in step with LearnEndpoints.Bury.
        return database.Cards.AsNoTracking().Where(card => card.UserId == userId
            && card.SuspendedReason == null
            && card.DueMs <= endOfLocalDayMs
            && (card.BuriedUntilLocalDate == null || string.Compare(card.BuriedUntilLocalDate, localToday) < 0)
            && activeSenseIds.Contains(card.SenseId)
            && database.Senses.Any(sense => sense.Id == card.SenseId
                && database.Words.Any(word => word.Id == sense.WordId && word.Status == "approved")));
    }

    // Plan §3.4 / H4: an en_es card is visible only when its es_en sibling reached Review and
    // was answered correctly on an earlier local date. Implemented as a predicate, so no
    // extra column and no back-fill is needed. Shared by Reviewable (cards already out of New)
    // and New (the introduction path): with the predicate applied in only one of them an en_es
    // card can never leave state New, which is exactly the T-20261018-0332 defect.
    internal static IQueryable<Card> DirectionVisible(PalabrasDbContext database, IQueryable<Card> cards,
        long startOfLocalDayMs)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(cards);
        return cards.Where(card => card.Direction == "es_en"
            || database.Cards.Any(sibling => sibling.UserId == card.UserId
                && sibling.SenseId == card.SenseId && sibling.Direction == "es_en"
                && sibling.State == CardState.Review
                && database.ReviewLogs.Any(log => log.CardId == sibling.Id && log.UserId == sibling.UserId
                    && log.CardEpoch == sibling.Epoch && log.CountsForSrs == true
                    && log.Rating > Rating.Again && log.ReviewedAtMs < startOfLocalDayMs)));
    }

    internal static IQueryable<Card> Reviewable(PalabrasDbContext database, IQueryable<Card> eligible, long startOfLocalDayMs)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        return DirectionVisible(database, eligible.Where(card => card.State != CardState.New), startOfLocalDayMs);
    }

    // Learning/Relearning first (short-term steps keep the session ordered), then due_ms.
    internal static IQueryable<Card> Due(PalabrasDbContext database, QueueBounds bounds, ActiveSenses active, int? limit)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        IQueryable<Card> query = Reviewable(database, Eligible(database, bounds, active), bounds.StartOfLocalDayMs)
            .OrderByDescending(card => card.State == CardState.Learning || card.State == CardState.Relearning)
            .ThenBy(card => card.DueMs);
        return limit is { } take ? query.Take(take) : query;
    }

    // New cards are introduced in plan §2.3 order (CEFR, then content order). Both directions are
    // admitted: `en_es` is not excluded outright, it is gated by the same §3.4 sibling predicate the
    // due path uses (T-20261018-0332). The new-card budget is a WORD budget (O-5 = no): an en_es
    // unlock is the same word in the other direction, so it does not spend a slot of new_per_day
    // (PlanComposer counts es_en introductions only). It does share the plan's `LIMIT`, so a
    // graduation-day round can be unlock-only; fresh words follow in the next round/day.
    internal static IQueryable<Card> New(PalabrasDbContext database, QueueBounds bounds, ActiveSenses active, int? limit)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var fresh = Eligible(database, bounds, active).Where(card => card.State == CardState.New);
        IQueryable<Card> query =
            from card in DirectionVisible(database, fresh, bounds.StartOfLocalDayMs)
            join sense in database.Senses on card.SenseId equals sense.Id
            join word in database.Words on sense.WordId equals word.Id
            // Within a band, the content rank ramps the session (plan §2). SQLite sorts NULL first
            // in ASC, so the coalesce is load-bearing: an unranked word must sort last, not first.
            // The sentinel is int.MaxValue, above the schema's 9999 ceiling, so no shipped rank
            // can tie with it.
            orderby word.Cefr, word.Difficulty ?? UnrankedDifficulty, word.Id, sense.SenseOrder
            select card;
        return limit is { } take ? query.Take(take) : query;
    }

    internal static Task<int> CountDueAsync(PalabrasDbContext database, QueueBounds bounds, ActiveSenses active,
        CancellationToken cancellationToken)
        => Reviewable(database, Eligible(database, bounds, active), bounds.StartOfLocalDayMs).CountAsync(cancellationToken);

    internal static async Task<int> CountDueOnLocalDateAsync(PalabrasDbContext database, QueueBounds bounds,
        ActiveSenses active, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        // Same Eligible predicate, re-opened to the requested window plus a lower bound.
        var window = bounds with { EndOfLocalDayMs = toMs };
        var from = fromMs;
        var eligible = Eligible(database, window, active).Where(card => card.DueMs >= from);
        return await Reviewable(database, eligible, window.StartOfLocalDayMs).CountAsync(cancellationToken);
    }

    internal static async Task<DateTimeOffset?> NextDueUtcAsync(PalabrasDbContext database, QueueBounds bounds,
        ActiveSenses active, CancellationToken cancellationToken)
    {
        var endOfLocalDayMs = bounds.EndOfLocalDayMs;
        // The day window is re-opened first: Eligible already caps due_ms at end of day.
        var open = bounds with { EndOfLocalDayMs = long.MaxValue };
        IQueryable<Card> later = Eligible(database, open, active).Where(card => card.DueMs > endOfLocalDayMs);
        var next = await Reviewable(database, later, bounds.StartOfLocalDayMs)
            .MinAsync(card => (long?)card.DueMs, cancellationToken);
        return next is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }
}

internal sealed record PlanOutcome(
    IReadOnlyList<PlanItem> Items, int Due, int NewOffered, int Backlog,
    IReadOnlyList<string> Warnings, DateTimeOffset? NextDueUtc);

// Plan §2.3 + §4: due first, then new. `introduced_local_date` is written by the attempt
// applier, never here, so fetching an abandoned plan does not burn the new-card quota.
internal static class PlanComposer
{
    // design.md:167 sizing assumptions: review ~8 s, new card ~30 s including steps.
    private const int ReviewSeconds = 8;
    private const int NewCardSeconds = 30;
    private const int MinimumBudgetSeconds = 60;

    internal static async Task<PlanOutcome> ComposeAsync(PalabrasDbContext database, QueueBounds bounds,
        ActiveSenses active, LearnerClock clock, UserSettings settings, int? maxNew, int? budgetS,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);

        var backlog = await LearnQueries.CountDueAsync(database, bounds, active, cancellationToken);
        var maxReviews = Math.Max(settings.MaxReviewsPerDay, 0);
        var dueCards = await LearnQueries.Due(database, bounds, active, maxReviews).ToListAsync(cancellationToken);

        // Plan §2.3 / O-5 = no (owner decision 2026-10-18, orchestrator): the locked cap is "12 new
        // WORDS per day", and an en_es unlock is the same word seen in the other direction, so only
        // es_en introductions spend the budget. The count is derived (no column): `introduced_local_date`
        // is still stamped on the first applied review of either direction (LearnAttempts.Apply) for
        // audit/stage purposes, but en_es rows are excluded here by construction.
        var introducedToday = await database.Cards.CountAsync(card =>
            card.UserId == bounds.UserId && card.Direction == "es_en"
            && card.IntroducedLocalDate == bounds.LocalToday, cancellationToken);
        // Hard ceiling for every path, explicit maxNew included: the request may never raise
        // user_settings.new_per_day (plan §4 maxNew + H11 "add new anyway", locked 12/day).
        var remainingNew = Math.Clamp(settings.NewPerDay - introducedToday, 0, Math.Max(settings.NewPerDay, 0));

        var warnings = new List<string>();
        var backlogBlocked = backlog > settings.BacklogThreshold;
        // H11: an explicit maxNew is the "add new anyway" override and outranks the backlog rule,
        // but only inside the daily cap above.
        var newLimit = backlogBlocked && maxNew is null ? 0 : remainingNew;
        if (maxNew is { } requested)
        {
            newLimit = Math.Min(Math.Max(requested, 0), remainingNew);
        }
        var sessionMax = Math.Max(settings.SessionMaxS ?? 900, MinimumBudgetSeconds);
        var target = Math.Clamp(budgetS ?? settings.SessionTargetS ?? 600, MinimumBudgetSeconds, sessionMax);
        if (maxNew is null)
        {
            // Automatic composition respects the time budget and the 900 s hard stop.
            // An explicit maxNew is the user-initiated H11 override; the hard stop still ends
            // the session client-side, plan §4/H10.
            var dueEstimate = (long)dueCards.Count * ReviewSeconds;
            newLimit = dueEstimate >= target ? 0 : Math.Min(newLimit, (int)((target - dueEstimate) / NewCardSeconds));
        }
        if (backlogBlocked && newLimit == 0)
        {
            // Machine key only. The sentence lives in the copy module (Frontend/src/copy/feedback.ts),
            // never in the API; counts.backlog is the number to interpolate (plan §4).
            warnings.Add(PlanWarnings.BacklogNoNew);
        }

        var newCards = newLimit > 0
            ? await LearnQueries.New(database, bounds, active, newLimit).ToListAsync(cancellationToken)
            : [];

        var ordered = dueCards.Concat(newCards).ToArray();
        var details = await LoadDetailsAsync(database, ordered, cancellationToken);
        var items = new List<PlanItem>(ordered.Length);
        foreach (var card in ordered)
        {
            if (!details.TryGetValue(card.Id, out var detail))
            {
                continue;
            }
            items.Add(ToPlanItem(card, detail.Sense, detail.Word));
        }

        var nextDue = dueCards.Count == 0
            ? await LearnQueries.NextDueUtcAsync(database, bounds, active, cancellationToken)
            : null;
        return new PlanOutcome(items, dueCards.Count, items.Count - dueCards.Count,
            backlog, warnings, nextDue);
    }

    internal static PlanItem ToPlanItem(Card card, Sense sense, Word word)
    {
        var cloze = ClozeDerivation.Derive(sense.ExampleEs, word.Headword);
        // Server-owned mode assignment (plan §4). Cloze alternates by reps so a production
        // card is not always typed; without an exact headword match it stays typed.
        var mode = card.Direction == "es_en"
            ? AttemptModes.Recognition
            : cloze is not null && card.Reps % 2 == 1 ? AttemptModes.Cloze : AttemptModes.Typed;
        // cardEpoch is the invalidation token (plan §3.2/§3.4): the client echoes it in the attempt
        // and a mismatch after a content change yields stale_epoch instead of a wrong FSRS update.
        return new PlanItem(card.Id, card.Epoch, card.Direction, mode, card.State == CardState.New,
            new PlanWord(word.Key, word.Headword, word.Article, word.Gender, word.Pos),
            new PlanSense(sense.EnGloss, sense.ExampleEs, sense.ExampleEn, cloze,
                AcceptedEs(sense, word), AcceptedEn(sense), sense.AccentsAreThePoint),
            new PlanNotes(word.RegionScope, word.Register));
    }

    // Plan §2.1: until a sense carries explicit accepted answers, ES is the lemma and EN is
    // the gloss split on ", ".
    private static string[] AcceptedEs(Sense sense, Word word)
    {
        var parsed = ParseStringArray(sense.EsAccepted);
        return parsed.Length > 0 ? parsed : [word.Headword];
    }

    private static string[] AcceptedEn(Sense sense)
    {
        var parsed = ParseStringArray(sense.EnAccepted);
        if (parsed.Length > 0)
        {
            return parsed;
        }
        return sense.EnGloss.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json);
            return values is null ? [] : values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<Dictionary<int, (Card Card, Sense Sense, Word Word)>> LoadDetailsAsync(
        PalabrasDbContext database, Card[] cards, CancellationToken cancellationToken)
    {
        if (cards.Length == 0)
        {
            return [];
        }
        var cardIds = cards.Select(card => card.Id).ToArray();
        var rows = await (from card in database.Cards.AsNoTracking()
                          join sense in database.Senses on card.SenseId equals sense.Id
                          join word in database.Words on sense.WordId equals word.Id
                          where cardIds.Contains(card.Id)
                          select new { Card = card, Sense = sense, Word = word }).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Card.Id, row => (row.Card, row.Sense, row.Word));
    }

    // design.md:154 stage ladder, furthest advanced card wins so a word with both
    // directions shows the direction the learner is furthest along.
    internal static string Stage(IEnumerable<Card> cards)
    {
        var list = cards.ToArray();
        if (list.Length == 0 || list.All(card => card.State == CardState.New))
        {
            return "nuevo";
        }
        if (list.Any(card => card.State == CardState.Review && (card.Stability ?? 0) >= 90))
        {
            return "dominada";
        }
        if (list.Any(card => card.State == CardState.Review && (card.Stability ?? 0) >= 21))
        {
            return "consolidada";
        }
        if (list.Any(card => card.State == CardState.Review))
        {
            return "reciente";
        }
        return "aprendiendo";
    }
}
