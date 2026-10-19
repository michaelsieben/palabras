namespace Palabras.Persistence;

// M3b council import (design-amendment.md ΔF:556-583): which models proposed and critiqued a specific
// word version. The composite key leads with word_id, so per-run reads use ix_word_provenance_run.
// council_run_id is NO ACTION, not CASCADE: a run is never deleted, and provenance must not disappear
// silently if one ever is.
internal sealed class WordProvenance
{
    public int WordId { get; set; }
    public int WordVersion { get; set; }
    public int CouncilRunId { get; set; }
    public string ModelId { get; set; } = "";
    public string Role { get; set; } = "";
    public string? Issues { get; set; }
}
