namespace Palabras.Learn;

// M6.6 stats v1 wire contract. Frozen in
// .opencode/runs/T-20261018-0332/04-stats-design.md §3: keep these names, nesting and types
// until the /progreso screen and its client validator exist.
//
// Two honesty rules are part of the shape, not of the copy:
// - no percentage is serialized. `retention` carries reviews/passed only, so the UI owns the
//   "Pocos datos todavía" cut-off (20 reviews) and an estimate is never rendered as exact.
// - `totals.answerMs` / `days[].answerMs` are ANSWER time (prompt -> show/submit), summed from
//   the client-reported `duration_ms`. They are a lower bound of time in the app: feedback
//   reading and rating time are excluded and a missing duration is skipped. The UI labels this
//   "tiempo respondiendo"; sitting time arrives with M7's daily record.
internal sealed record StatsResponse(
    string LocalDate,
    string From,
    string To,
    StatsTotals Totals,
    IReadOnlyList<StatsDay> Days,
    IReadOnlyList<StatsMode> ByMode,
    StatsRetention Retention,
    StatsWords Words,
    IReadOnlyList<StatsHardestWord> Hardest,
    StatsQuality Quality);

internal sealed record StatsTotals(
    int Answers, int Hits, int Misses, int FormSlips, int Overridden,
    int FirstTry, int SecondLook, long AnswerMs, int ActiveDays, int NewWords);

// Dense: one entry per local date from..to, zeros included. `newWords` counts es_en
// introductions stamped on that local date (the O-5 rule: an en_es unlock is the same word and
// never spends the 12-word budget), so it can be read per day.
internal sealed record StatsDay(string Date, int Answers, int Hits, int Misses, int NewWords, long AnswerMs);

// One entry per stored mode string actually present, ordered by name. Grouping by the stored
// string (never an enum) keeps a future mode (M6.5 `choice`) from needing a stats change.
internal sealed record StatsMode(string Mode, int Answers, int Hits, int Misses, int FormSlips);

// Pass rate on mature cards (`state_before = Review`, applied non-practice rows). The ratio is
// computed client-side and hidden below 20 reviews; `target` is user_settings.desired_retention.
// `esEn` rows are self-graded recognition, `enEs` rows are checked typed/cloze answers, so the
// split by card direction is what keeps a self-grade from reading as measured recall.
internal sealed record StatsRetention(double Target, StatsRetentionSlice All, StatsRetentionSlice EsEn, StatsRetentionSlice EnEs);

internal sealed record StatsRetentionSlice(int Reviews, int Passed);

// Snapshot of current state, not a range figure. Approved words with at least one active sense
// only, exactly like GET /api/learn/words. `suspendedCards` reports the learner-owned reasons
// only: word_not_approved / sense_removed are content state and never surface here.
internal sealed record StatsWords(
    int Approved,
    StatsStages Stages,
    int LeechCards,
    StatsSuspended SuspendedCards);

internal sealed record StatsStages(int Nuevo, int Aprendiendo, int Reciente, int Consolidada, int Dominada);

internal sealed record StatsSuspended(int Manual, int Leech);

internal sealed record StatsHardestWord(string Key, string? Article, string Headword, string Gloss, int Misses);

// lateRows: rows whose received_at_ms is more than 23 h after reviewed_at_ms, i.e. a flush that
// hit the [now-24h, now+5min] clamp (LearnerClock.cs:32-38) and may sit in the wrong day bucket.
// missingDurationRows: rows with no duration_ms; they contribute nothing to answerMs.
internal sealed record StatsQuality(int LateRows, int MissingDurationRows);
