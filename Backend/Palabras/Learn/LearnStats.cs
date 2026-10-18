using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// M6.6 stats v1 (design §1/§3). Read-only: no migration, no column, no rollup table.
//
// Every figure is counted from committed `review_log` / `cards` rows. Three caveats are part of
// the contract and are repeated in the response doc comments, so a number is never mistaken for
// something it is not:
//
// 1. TIME IS ANSWER TIME, NOT TIME IN THE APP. `duration_ms` is client-reported (prompt ->
//    "Mostrar respuesta" for recognition, prompt -> submit for typed/cloze) and is capped at
//    600 000 ms by the client and again by LearnAttempts.Normalize (LearnAttempts.cs:38).
//    Feedback reading and rating time are excluded, and NULL is skipped, so the sum is a LOWER
//    BOUND. The field is named `answerMs` and the UI says "tiempo respondiendo"; real sitting
//    time needs M7's practice_sessions table (design §2).
//
// 2. DAY BUCKETS CAN BE AN ESTIMATE. A bucket comes from `reviewed_at_ms` through
//    LearnerClock.LocalDateOf, but that instant was clamped to [now-24h, now+5min] when the
//    offline queue flushed (LearnerClock.cs:32-38). An answer flushed more than 24 h late
//    therefore lands on the clamp day, not the real one. `quality.lateRows` counts the rows
//    where received_at_ms - reviewed_at_ms > 23 h, so the frontend can footnote them.
//
// 3. RETENTION IS A PASS RATE, NOT PROOF OF RECALL. It is `rating > Again` over applied rows
//    whose `state_before` is Review. es_en rows are self-graded, so the split by direction is
//    reported alongside and no percentage is computed here (the UI hides it below 20 reviews).
//
// Activity counts (answers/hits/misses/time) include every stored row, including rejected
// (counts_for_srs = 0) rows, because the learner did answer them. Retention uses applied rows
// only, and word-level sections (words, hardest) are approved words only
// (design-amendment.md:349: nothing non-approved appears in the learner UI, stats included).
//
// Sequencing note: the M6.5 S2 `practice` column does not exist yet. When it lands, practice
// rows are already excluded from retention by `counts_for_srs = 0` (LearnQueries.cs:88-92 and
// 01-variety-design.md:169); they need only to be surfaced as their own byMode entry.
internal static class LearnStats
{
    // Mirrors UserSettings.DesiredRetention (UserSettings.cs:6) for a learner with no row yet.
    private const double DefaultDesiredRetention = 0.90;
    private const string DateFormat = "yyyy-MM-dd";
    // design §3 quality.lateRows: a flush this far behind the client instant hit the clamp.
    private static readonly long LateFlushThresholdMs = (long)TimeSpan.FromHours(23).TotalMilliseconds;

    // One `review_log` row, projected in SQL to the columns the aggregator needs. `IsCorrectForm`
    // is the only correctness flag used: hit/miss follows the self-grade (O-14), and a checked
    // answer's form slip is reported as its own number. Checked vs self-graded answers are
    // separated by `Mode`, which is what the design's byMode section already does.
    internal sealed record StatsRow(
        int CardId, int WordId, string? SessionId, Rating Rating, long ReviewedAtMs, long ReceivedAtMs,
        int? DurationMs, string Mode, bool? IsCorrectForm, bool Overridden, bool CountsForSrs,
        CardState StateBefore);

    // A card on an approved word's active sense, with the word id resolved. The Card entity is
    // kept whole so `PlanComposer.Stage` (LearnQueries.cs:290-310) is reused verbatim: the stage
    // counts on /progreso and the chips on /palabras can never disagree.
    internal sealed record StatsCardRef(Card Card, int WordId);

    internal sealed record StatsWord(int Id, string Key, string? Article, string Headword, string Gloss);

    internal sealed record StatsInput(
        IReadOnlyList<StatsRow> Rows,
        IReadOnlyList<Rating> FirstRowRatings,
        IReadOnlyList<StatsCardRef> Cards,
        IReadOnlyList<StatsWord> Words,
        IReadOnlyDictionary<int, string> Directions,
        IReadOnlyList<string> IntroducedLocalDates,
        double DesiredRetention);

    // Range + projection happen in SQL (`IX(user_id, reviewed_at_ms)`, PalabrasDbContext.cs:220);
    // the per-day / per-mode grouping is in memory on purpose (design §3: a pure aggregator is
    // what the tests can call without a database). The widest admissible range is 366 days at the
    // 200 reviews/day cap, i.e. one indexed range scan. No predicate is evaluated client-side; an
    // untranslatable predicate throws (EF Core 3.0+), which the
    // StatsQueriesRunOnSqliteWithoutClientEvaluation test relies on.
    internal static async Task<StatsInput> LoadAsync(PalabrasDbContext database, int userId, LearnerClock clock,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        var startMs = clock.StartOfLocalDateUtc(from).ToUnixTimeMilliseconds();
        var endMs = clock.EndOfLocalDateUtc(to).ToUnixTimeMilliseconds();
        var fromText = from.ToString(DateFormat, CultureInfo.InvariantCulture);
        var toText = to.ToString(DateFormat, CultureInfo.InvariantCulture);

        var rows = await database.ReviewLogs.AsNoTracking()
            .Where(log => log.UserId == userId && log.ReviewedAtMs >= startMs && log.ReviewedAtMs <= endMs)
            .Select(log => new StatsRow(log.CardId, log.WordId, log.SessionId, log.Rating, log.ReviewedAtMs,
                log.ReceivedAtMs, log.DurationMs, log.Mode, log.IsCorrectForm, log.Overridden,
                log.CountsForSrs == true, log.StateBefore))
            .ToListAsync(cancellationToken);

        // First try / second look is a classification of the FIRST row of each
        // (session_id, card_id) attempt group, exactly like sessionTotals
        // (sessionMachine.ts:111-116, design §1 row "first try vs second look", §7 "stats mirrors
        // sessionTotals"). The group minimum is expressed in SQL rather than re-sorted in memory,
        // so the rule stays translatable and only one rating per group travels back:
        //
        //   session_id IS NULL
        //   OR NOT EXISTS (SELECT 1 FROM review_log o
        //                  WHERE o.card_id = r.card_id AND o.session_id = r.session_id
        //                    AND (o.reviewed_at_ms < r.reviewed_at_ms
        //                         OR (o.reviewed_at_ms = r.reviewed_at_ms AND o.id < r.id)))
        //
        // Two properties matter. (a) The id tie-break makes the predicate select exactly one row
        // per group even if two attempts share a millisecond. (b) `session_id` is nullable and EF
        // rewrites column equality with C# null semantics (`a = b OR (a IS NULL AND b IS NULL)`),
        // so a bare NOT EXISTS would fold every NULL-session row into one group. The explicit
        // `SessionId == null` branch is what makes each NULL row its own group, i.e. the design's
        // rule, and it is the only non-obvious part of this predicate. Same range filter as the
        // row query, so a group is defined over the requested range only.
        var rangeLogs = database.ReviewLogs.AsNoTracking()
            .Where(log => log.UserId == userId && log.ReviewedAtMs >= startMs && log.ReviewedAtMs <= endMs);
        var firstRowRatings = await rangeLogs
            .Where(log => log.SessionId == null
                || !rangeLogs.Any(other => other.CardId == log.CardId
                    && other.SessionId == log.SessionId
                    && (other.ReviewedAtMs < log.ReviewedAtMs
                        || (other.ReviewedAtMs == log.ReviewedAtMs && other.Id < log.Id))))
            .Select(log => log.Rating)
            .ToListAsync(cancellationToken);

        // Retention splits by direction, and a review may belong to a card whose word was
        // demoted later: the direction lookup is therefore independent of the approved filter
        // below. Keyed on the range's cards only, so the IN list stays the learner's card count.
        var cardIds = rows.Select(row => row.CardId).Distinct().ToArray();
        var directions = cardIds.Length == 0
            ? new Dictionary<int, string>()
            : await database.Cards.AsNoTracking()
                .Where(card => card.UserId == userId && cardIds.Contains(card.Id))
                .Select(card => new { card.Id, card.Direction })
                .ToDictionaryAsync(row => row.Id, row => row.Direction, cancellationToken);

        // O-5: only es_en introductions are new WORDS; an en_es unlock is the same word seen in
        // the other direction. Same rule as the budget counter (LearnQueries.cs:164-166), which
        // is why the stamp is read here and not derived from the review rows. `yyyy-MM-dd` is
        // fixed width, so the string comparison is a date comparison; `string.Compare` is the
        // proven SQLite-translatable form (LearnQueries.cs:46).
        var introduced = await database.Cards.AsNoTracking()
            .Where(card => card.UserId == userId && card.Direction == "es_en"
                && card.IntroducedLocalDate != null
                && string.Compare(card.IntroducedLocalDate, fromText) >= 0
                && string.Compare(card.IntroducedLocalDate, toText) <= 0)
            .Select(card => card.IntroducedLocalDate!)
            .ToListAsync(cancellationToken);

        // Approved corpus, gated on active senses exactly like GET /api/learn/words
        // (LearnEndpoints.cs:212,233-238,254): a retained removed sense is not learner content.
        var active = await ActiveSenses.LoadAsync(database, null, cancellationToken);
        var activeSenseIds = active.SenseIds.ToArray();

        var cards = await (from card in database.Cards.AsNoTracking()
                           join sense in database.Senses on card.SenseId equals sense.Id
                           join word in database.Words on sense.WordId equals word.Id
                           where card.UserId == userId && word.Status == "approved"
                               && activeSenseIds.Contains(sense.Id)
                           select new { Card = card, sense.WordId }).ToListAsync(cancellationToken);

        // First active sense by sense_order is the gloss, as WordsAsync does.
        var wordRows = await (from word in database.Words.AsNoTracking()
                              join sense in database.Senses on word.Id equals sense.WordId
                              where word.Status == "approved" && activeSenseIds.Contains(sense.Id)
                              orderby word.Id, sense.SenseOrder
                              select new { word.Id, word.Key, word.Article, word.Headword, sense.EnGloss })
            .ToListAsync(cancellationToken);
        var words = wordRows.GroupBy(row => row.Id)
            .Select(group => new StatsWord(group.Key, group.First().Key, group.First().Article,
                group.First().Headword, group.First().EnGloss))
            .ToArray();

        var settings = await database.UserSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);

        return new StatsInput(rows,
            firstRowRatings,
            cards.Select(row => new StatsCardRef(row.Card, row.WordId)).ToArray(),
            words,
            directions,
            introduced,
            settings?.DesiredRetention ?? DefaultDesiredRetention);
    }

    // Pure over in-memory rows (design §3): no database, no clock read, so every rule below is
    // testable with hand-built rows. `from`/`to` are inclusive local dates.
    internal static StatsResponse Aggregate(StatsInput input, LearnerClock clock, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(clock);

        var dayCount = to.DayNumber - from.DayNumber + 1;
        var dayAnswers = new int[dayCount];
        var dayHits = new int[dayCount];
        var dayMisses = new int[dayCount];
        var dayNewWords = new int[dayCount];
        var dayAnswerMs = new long[dayCount];

        foreach (var introduction in input.IntroducedLocalDates)
        {
            if (!DateOnly.TryParseExact(introduction, DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                // The SQL filter already bounds the range; an unparsable stamp is corpus damage,
                // not a learner figure, so it is skipped instead of failing the whole request.
                continue;
            }
            var index = date.DayNumber - from.DayNumber;
            if (index >= 0 && index < dayCount)
            {
                dayNewWords[index]++;
            }
        }

        foreach (var row in input.Rows)
        {
            var date = clock.LocalDateOf(DateTimeOffset.FromUnixTimeMilliseconds(row.ReviewedAtMs));
            var index = date.DayNumber - from.DayNumber;
            // Defensive only: the ms window is built from these very local-day boundaries, so a
            // row inside it always maps inside the range.
            if (index < 0 || index >= dayCount)
            {
                continue;
            }
            dayAnswers[index]++;
            if (row.Rating > Rating.Again)
            {
                dayHits[index]++;
            }
            else
            {
                dayMisses[index]++;
            }
            dayAnswerMs[index] += row.DurationMs ?? 0;
        }

        var days = new StatsDay[dayCount];
        for (var index = 0; index < dayCount; index++)
        {
            var date = from.AddDays(index);
            days[index] = new StatsDay(date.ToString(DateFormat, CultureInfo.InvariantCulture),
                dayAnswers[index], dayHits[index], dayMisses[index], dayNewWords[index], dayAnswerMs[index]);
        }

        // O-14: a hit is any rating above Again (Hard/Good/Easy). Overrides are hits AND are
        // reported separately, so the inflation is visible rather than folded in.
        var hits = input.Rows.Count(row => row.Rating > Rating.Again);
        var misses = input.Rows.Count(row => row.Rating == Rating.Again);
        var formSlips = input.Rows.Count(row => row.Rating > Rating.Again && row.IsCorrectForm == false);
        var overridden = input.Rows.Count(row => row.Overridden);

        // First try vs second look mirrors sessionTotals (Frontend/src/learn/sessionMachine.ts:111-116,
        // design §1 "first try vs second look", §7 "stats mirrors sessionTotals"): the FIRST row of
        // each (session_id, card_id) attempt group decides, and a first row rating of Again is a
        // second look, not a right first try. A requeue row after an Again therefore lands in
        // `answers` only, never in `firstTry`. The group minimum and the NULL-session rule are
        // computed in SQL (LearnStats.LoadAsync), so only one rating per group arrives here:
        // `firstTry + secondLook` is the group count, which is deliberately not `answers`.
        var firstTry = input.FirstRowRatings.Count(rating => rating > Rating.Again);
        var secondLook = input.FirstRowRatings.Count(rating => rating == Rating.Again);

        var byMode = input.Rows
            .GroupBy(row => row.Mode, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StatsMode(group.Key, group.Count(),
                group.Count(row => row.Rating > Rating.Again),
                group.Count(row => row.Rating == Rating.Again),
                group.Count(row => row.Rating > Rating.Again && row.IsCorrectForm == false)))
            .ToArray();

        // Retention: applied rows only (rejected rows never moved the card, LearnAttempts.cs:256),
        // and only mature reviews, so a first sighting of a word cannot flatter the number.
        // `all` counts every mature applied row; the two direction splits additionally need the
        // card row, so a row whose card is gone from `Directions` (orphan; review_log.card_id is
        // OnDelete(NoAction), PalabrasDbContext.cs:194) is in `all` but in neither split. That
        // makes `all.reviews >= esEn + enEs` by design — the splits are a breakdown, not a
        // partition — and it cannot happen while cards are never hard-deleted.
        var mature = input.Rows.Where(row => row.CountsForSrs && row.StateBefore == CardState.Review).ToArray();
        int esEnReviews = 0, esEnPassed = 0, enEsReviews = 0, enEsPassed = 0;
        foreach (var row in mature)
        {
            var passed = row.Rating > Rating.Again;
            if (!input.Directions.TryGetValue(row.CardId, out var direction))
            {
                continue;
            }
            if (direction == "es_en")
            {
                esEnReviews++;
                if (passed)
                {
                    esEnPassed++;
                }
            }
            else if (direction == "en_es")
            {
                enEsReviews++;
                if (passed)
                {
                    enEsPassed++;
                }
            }
        }
        var retention = new StatsRetention(input.DesiredRetention,
            new StatsRetentionSlice(mature.Length, mature.Count(row => row.Rating > Rating.Again)),
            new StatsRetentionSlice(esEnReviews, esEnPassed),
            new StatsRetentionSlice(enEsReviews, enEsPassed));

        var wordById = input.Words.ToDictionary(word => word.Id);
        var cardsByWord = input.Cards.GroupBy(card => card.WordId)
            .ToDictionary(group => group.Key, group => group.Select(card => card.Card).ToArray());
        var stageCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["nuevo"] = 0, ["aprendiendo"] = 0, ["reciente"] = 0, ["consolidada"] = 0, ["dominada"] = 0
        };
        foreach (var word in input.Words)
        {
            stageCounts[PlanComposer.Stage(cardsByWord.GetValueOrDefault(word.Id, []))]++;
        }
        var words = new StatsWords(input.Words.Count,
            new StatsStages(stageCounts["nuevo"], stageCounts["aprendiendo"], stageCounts["reciente"],
                stageCounts["consolidada"], stageCounts["dominada"]),
            input.Cards.Count(card => card.Card.IsLeech),
            new StatsSuspended(input.Cards.Count(card => card.Card.SuspendedReason == "manual"),
                input.Cards.Count(card => card.Card.SuspendedReason == "leech")));

        // Hardest: misses grouped by word, approved+active words only, so a demoted word's history
        // stays inside the totals but is never named (design §1 interpretation).
        var hardest = input.Rows
            .Where(row => row.Rating == Rating.Again && wordById.ContainsKey(row.WordId))
            .GroupBy(row => row.WordId)
            .Select(group => new { Word = wordById[group.Key], Misses = group.Count() })
            .OrderByDescending(item => item.Misses)
            .ThenBy(item => item.Word.Headword, StringComparer.Ordinal)
            .ThenBy(item => item.Word.Id)
            .Take(5)
            .Select(item => new StatsHardestWord(item.Word.Key, item.Word.Article, item.Word.Headword,
                item.Word.Gloss, item.Misses))
            .ToArray();

        var quality = new StatsQuality(
            input.Rows.Count(row => row.ReceivedAtMs - row.ReviewedAtMs > LateFlushThresholdMs),
            input.Rows.Count(row => row.DurationMs is null));

        return new StatsResponse(
            clock.LocalToday.ToString(DateFormat, CultureInfo.InvariantCulture),
            from.ToString(DateFormat, CultureInfo.InvariantCulture),
            to.ToString(DateFormat, CultureInfo.InvariantCulture),
            new StatsTotals(input.Rows.Count, hits, misses, formSlips, overridden,
                firstTry, secondLook,
                input.Rows.Sum(row => (long)(row.DurationMs ?? 0)),
                dayAnswers.Count(count => count > 0),
                dayNewWords.Sum()),
            days,
            byMode,
            retention,
            words,
            hardest,
            quality);
    }
}
