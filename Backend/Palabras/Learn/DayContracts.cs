namespace Palabras.Learn;

// M7 wire contract. The skip request and its status codes are specified by
// design-amendment.md ΔD:280-289: body {local_date, source}, 200 skipped, 200 idempotent when
// today is already skipped, 409 {reason:'skip_unavailable', next_available}, 409
// {reason:'already_done'}. The GET route and response shape are not specified anywhere; this is
// the recommendation recorded in .opencode/runs/T-20261018-2005/10-m7-backend.md (O-M7-1).
// JSON is camelCase like every other /api/learn route (07-learn-api.md:14).
internal sealed record SkipRequest(string? LocalDate, string? Source);

// Completion signal for design.md:171 ("done when the composed session is completed"). The
// route and body are M7 additions, not documented: see O-M7-2 in the run artifact.
internal sealed record DayDoneRequest(int? DurationS, int? PlannedS, int? NewCount, int? ReviewCount);

// `status` is null (serialized explicitly, not omitted) when today has no day_status row yet.
// `skipNextAvailable` is the first local date a new skip is allowed (ΔD:283), null while one
// is available — the same two keys the M8 push payload carries (ΔC.2 payload, design-amendment.md:217).
internal sealed record DayStatusResponse(
    string LocalDate,
    string? Status,
    int Streak,
    bool SkipAvailable,
    string? SkipNextAvailable);

internal static class DayStatusValues
{
    internal const string Done = "done";
    internal const string Skipped = "skipped";
    internal const string Missed = "missed";

    // A day that keeps the streak chain alive (ΔD:290): done increments it, skipped only carries it.
    internal static bool KeepsChain(string status) => status is Done or Skipped;
}

internal static class SkipSources
{
    internal const string App = "app";
    internal const string Notification = "notification";

    internal static bool IsKnown(string? source) => source is App or Notification;
}
