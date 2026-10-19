namespace Palabras.Persistence;

// M4 reviewer gate (design-amendment.md ΔF:609-618, design.md:129-135): append-only record of every
// reviewer verdict. client_review_id is the offline replay key, so a retried action is a duplicate,
// never a second decision. This table is M4-owned; council_run_id has no foreign key until M3b adds
// council_runs.
internal sealed class WordReview
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public int WordVersion { get; set; }
    public int? SenseOrder { get; set; }
    public int ReviewerId { get; set; }
    public string Action { get; set; } = "";
    public string? Field { get; set; }
    public int? ChosenAlternativeId { get; set; }
    public string? EditDiff { get; set; }
    public string? Note { get; set; }
    public string ClientReviewId { get; set; } = "";
    public int ViaBatch { get; set; }
    public int? CouncilRunId { get; set; }
    public double? AgreementAtReview { get; set; }
    public int? DecisionMs { get; set; }
    public long CreatedAt { get; set; }
}
