namespace Palabras.Learn;

// M7 wire contract. The skip request and its status codes are specified by
// design-amendment.md ΔD:280-289: body {local_date, source}, 200 skipped, 200 idempotent when
// today is already skipped, 409 {reason:'skip_unavailable', next_available}, 409
// {reason:'already_done'}. The GET route and response shape are not specified anywhere; this is
// the recommendation recorded in .opencode/runs/T-20261018-2005/10-m7-backend.md (O-M7-1).
// JSON is camelCase like every other /api/learn route (07-learn-api.md:14).
// O-M7-5 (T-20261018-2105, decided): ΔD:288 sketches the 409 body in snake_case
// ({reason, next_available}); the established serialization convention of this API — and the
// documented global rule (07-learn-api.md:11-17, adopted by the step-02 docs) — is camelCase,
// so `nextAvailable` stays camelCase and ΔD:288 is the outlier. No snake_case alias ships
// without a consumer need; the M8 service worker branches on the status code only.
internal sealed record SkipRequest(string? LocalDate, string? Source);

// Completion signal for design.md:171 ("done when the composed session is completed"). The
// route and body are M7 additions, not documented: see O-M7-2 in the run artifact.
// O-M7-9 (C5, T-20261018-2105): `localDate` is optional — absent means the receipt day (the
// pre-M7-9 behavior, so existing callers are untouched). When present it must be exactly
// `yyyy-MM-dd` and one of the learner's two local days — today or yesterday (the bounded
// late-delivery window for an offline flush that lands after midnight). Writes, idempotent
// dedupe and the skip refund use that target date, not the receipt date, and the 200 payload
// describes the target day; anything else is refused with 400 and completes nothing.
internal sealed record DayDoneRequest(int? DurationS, int? PlannedS, int? NewCount, int? ReviewCount,
    string? LocalDate = null);

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
