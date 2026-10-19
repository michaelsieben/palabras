namespace Palabras.Persistence;

// M3b council import (design-amendment.md ΔF:556-583): one row per accepted council bundle. run_id is
// the manifest ULID and the import idempotency key; created_at is the manifest instant, imported_at is
// when this database accepted it. Instants are unix-ms INTEGER, like every other instant in the repo.
internal sealed class CouncilRun
{
    public int Id { get; set; }
    public string RunId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string PromptVersions { get; set; } = "";
    public string StyleGuideSha256 { get; set; } = "";
    public string? Topic { get; set; }
    public string? Cefr { get; set; }
    public int? RequestedCount { get; set; }
    public int ModelsRequested { get; set; }
    public int ModelsSucceededPropose { get; set; }
    public int ModelsSucceededCritique { get; set; }
    public int ReducedConfidence { get; set; }
    public int CandidatesCount { get; set; }
    public int DroppedCount { get; set; }
    public string CandidatesSha256 { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ImportedAt { get; set; }
}
