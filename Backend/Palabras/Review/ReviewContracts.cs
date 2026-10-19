using System.Text.Json.Serialization;

namespace Palabras.Review;

// Wire contract for /api/review/* (M4 plan §2.4, T-20261019-1508). camelCase like the rest of the
// API; optional members are omitted instead of serialized as null so the TypeScript `?:` shape
// stays `undefined`-shaped on the client.

internal sealed record ReviewSenseItem(
    int Order,
    string Gloss,
    IReadOnlyList<string> AcceptedEn,
    IReadOnlyList<string> AcceptedEs,
    string ExampleEs,
    string ExampleEn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes);

// `field` uses the ΔF reviewer vocabulary, not the legacy `word_alternatives.kind`:
// synonym/near-synonym/different-sense -> lemma, regional -> regional_variant,
// register -> register_variant. `support` is "<proposing models>/<models_requested>" for a council
// word (T-12, e.g. "2/4") and omitted for a legacy row, whose support column is the default 0.
internal sealed record ReviewAlternativeChip(
    int Id,
    string Field,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Translation,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Support);

internal sealed record ReviewLastFlag(
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    DateTimeOffset At);

internal sealed record ReviewItem(
    int WordId,
    string Key,
    int Version,
    string Headword,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Article,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Gender,
    string Pos,
    string Cefr,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Topic,
    IReadOnlyList<ReviewSenseItem> Senses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RegionNotes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RegisterNotes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Agreement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Band,
    bool ReducedConfidence,
    IReadOnlyList<string> LintWarnings,
    IReadOnlyList<ReviewAlternativeChip> Alternatives,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewLastFlag? LastFlag,
    // Additive (T-20261019-2100): the queue now leads with pending rows and then exposes approved
    // rows for objection, so the client must be able to tell them apart. Defaults keep every
    // pre-existing pending item serialising the same value it always implicitly had.
    string Status = "pending_review");

internal sealed record ReviewTopicCount(string Topic, int Pending);

// `approvedTotal`, `nextAfter` and `rejectedTotal` are additive: a caller that ignored them before
// keeps working, and the default scope (`status=pending`) is exactly the pre-trust-by-default page.
// `rejectedTotal` is the withdrawn-word count for the selected scope (`status=rejected` and
// `status=all`), so the objection UI can show "Retiradas (n)" without another request.
//
// `nextAfter` is an opaque cursor string (`p:<wordId>` for the pending block, `d:<wordId>` for the
// decided list) and must be echoed back as `after` unchanged. It is not a bare word id: the two
// lists are ordered independently, so the kind has to travel with the id.
internal sealed record ReviewQueueResponse(
    IReadOnlyList<ReviewItem> Items,
    int PendingTotal,
    IReadOnlyList<ReviewTopicCount> Topics,
    int ApprovedTotal = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextAfter = null,
    int RejectedTotal = 0);

internal sealed record ReviewEditRequest(string? Field, int? SenseOrder, string? After);

internal sealed record ReviewActionRequest(
    string? ClientReviewId,
    int WordId,
    int WordVersion,
    string? Action,
    string? Field,
    int? SenseOrder,
    int? AlternativeId,
    IReadOnlyList<ReviewEditRequest?>? Edits,
    string? Note,
    int? DecisionMs,
    bool? ViaBatch);

internal sealed record ReviewActionsRequest(IReadOnlyList<ReviewActionRequest?>? Actions);

internal sealed record ReviewActionWordResult(string Status, int Version);

internal sealed record ReviewActionResult(
    string ClientReviewId,
    string Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewActionWordResult? Word);

internal sealed record ReviewActionsResponse(IReadOnlyList<ReviewActionResult> Results);

// `version` is the current words.version: every review action (demote included) must echo it, so the
// approved list has to carry it or an approved row with version > 1 could never be demoted.
internal sealed record ApprovedWordItem(
    int WordId,
    string Key,
    int Version,
    string Headword,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Article,
    string Gloss,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Topic,
    DateTimeOffset ApprovedAt);

internal sealed record ApprovedWordsResponse(IReadOnlyList<ApprovedWordItem> Items);

// Per-item result values, closed set (plan §2.4). `invalid` covers an unusable item and an unknown
// word id; there is no per-item 404 because the batch always answers 200 with one row per action.
internal static class ReviewResults
{
    internal const string Applied = "applied";
    internal const string Duplicate = "duplicate";
    internal const string StaleVersion = "stale_version";
    internal const string InvalidTransition = "invalid_transition";
    internal const string Invalid = "invalid";
}

internal static class ReviewActionNames
{
    internal const string Approve = "approve";
    internal const string ApproveAlt = "approve_alt";
    internal const string ApproveEdit = "approve_edit";
    internal const string Reject = "reject";
    internal const string NeedsChange = "needs_change";
    internal const string Demote = "demote";

    internal static bool IsKnown(string? action) =>
        action is Approve or ApproveAlt or ApproveEdit or Reject or NeedsChange or Demote;
}

// The ΔF field vocabulary enforced by ck_word_reviews_field. Validating here keeps a bad field a
// per-item `invalid` instead of a CHECK-constraint 500.
internal static class ReviewFieldNames
{
    internal const string Lemma = "lemma";
    internal const string Gloss = "gloss";
    internal const string Example = "example";
    internal const string RegionalNote = "regional_note";
    internal const string RegionalVariant = "regional_variant";
    internal const string RegisterVariant = "register_variant";
    internal const string Article = "article";
    internal const string Gender = "gender";
    internal const string Accepted = "accepted";
    internal const string Whole = "whole";

    internal static bool IsKnown(string? field) => field is Lemma or Gloss or Example or RegionalNote
        or RegionalVariant or RegisterVariant or Article or Gender or Accepted or Whole;

    // approve_edit only edits stored content columns; chip-only fields (variants/whole) are not
    // free-text targets.
    internal static bool IsEditable(string? field) => field is Lemma or Gloss or Example
        or RegionalNote or Article or Gender or Accepted;
}
