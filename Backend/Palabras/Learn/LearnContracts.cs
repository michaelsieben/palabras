using System.Text.Json.Serialization;

namespace Palabras.Learn;

// Wire contract for /api/learn/*. Shapes are frozen in
// .opencode/runs/T-20261018-0124/02-retention-plan.md §4 (PlanItem, Attempt).
// Optional members are omitted rather than serialized as null so the TypeScript
// `?:` members stay `undefined`-shaped on the client.

internal sealed record PlanWord(string Key, string Headword, string? Article, string? Gender, string Pos);

internal sealed record PlanSense(
    string Gloss,
    string ExampleEs,
    string ExampleEn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClozeEs,
    IReadOnlyList<string> AcceptedEs,
    IReadOnlyList<string> AcceptedEn,
    bool AccentsAreThePoint);

internal sealed record PlanNotes(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Register);

internal sealed record PlanItem(
    int CardId, int CardEpoch, string Direction, string Mode, bool IsNew,
    PlanWord Word, PlanSense Sense, PlanNotes Notes);

internal sealed record PlanCounts(int Due, int NewOffered, int Backlog);

internal sealed record PlanResponse(
    string SessionId,
    DateTimeOffset GeneratedAtUtc,
    string LocalDate,
    IReadOnlyList<PlanItem> Items,
    PlanCounts Counts,
    IReadOnlyList<string> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? NextDueUtc);

internal sealed record AttemptRequest(
    string? ClientAttemptId, int CardId, int CardEpoch, string? SessionId, string? Mode, int Rating,
    bool? IsCorrectMeaning, bool? IsCorrectForm, bool? Overridden, string? FeedbackKind,
    string? AnswerText, int? DurationMs, DateTimeOffset? ReviewedAtUtc);

internal sealed record AttemptsRequest(IReadOnlyList<AttemptRequest?>? Attempts);

internal sealed record AttemptCardResult(int Id, int State, DateTimeOffset DueUtc, double IntervalDays, bool Leech);

internal sealed record AttemptResult(
    string ClientAttemptId, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttemptCardResult? Card);

internal sealed record AttemptsResponse(IReadOnlyList<AttemptResult> Results);

internal sealed record ForecastDay(string Date, int Due);

internal sealed record ForecastResponse(int Tomorrow, IReadOnlyList<ForecastDay> Next7Days);

internal sealed record WordListItem(
    string Key, string? Article, string Headword, string Gloss,
    IReadOnlyList<string> Topics, string Stage);

internal sealed record WordsResponse(IReadOnlyList<WordListItem> Items);

internal static class AttemptStatus
{
    internal const string Applied = "applied";
    internal const string Duplicate = "duplicate";
    internal const string StaleEpoch = "stale_epoch";
    internal const string Suspended = "suspended";
    internal const string OutOfOrder = "out_of_order";
}

internal static class AttemptModes
{
    internal const string Recognition = "recognition";
    internal const string Typed = "typed";
    internal const string Cloze = "cloze";

    internal static bool IsKnown(string? mode) => mode is Recognition or Typed or Cloze;
}

internal static class PlanWarnings
{
    // H11: backlog above user_settings.backlog_threshold suppresses new cards.
    internal const string BacklogNoNew = "backlog_no_new";
}
