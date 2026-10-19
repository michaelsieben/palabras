namespace Palabras.Persistence;

internal sealed class Word
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Headword { get; set; } = "";
    public string? Article { get; set; }
    public string? Gender { get; set; }
    public string Pos { get; set; } = "";
    public string Cefr { get; set; } = "";
    // Intra-band authoring rank (lower = taught earlier), content-level `difficulty`. NULL for a file
    // that carries no rank; the new-card ordering coalesces it to 999 so an unranked word sorts last.
    public int? Difficulty { get; set; }
    public string RegionScope { get; set; } = "";
    public string Register { get; set; } = "";
    public string? RegisterNotes { get; set; }
    public string? RegionNotes { get; set; }
    public string? Notes { get; set; }
    public string? Plural { get; set; }
    public string Status { get; set; } = "pending_review";
    public int Version { get; set; } = 1;
    public string ContentHash { get; set; } = "";
    public string ProvenanceMethod { get; set; } = "";
    public string? ProvenanceRun { get; set; }
    // M4 reviewer gate (ΔF:556-583): the M4 UI reads these. They stay empty for the hand-authored
    // corpus; M3b's council import is what fills them.
    public double? CouncilAgreement { get; set; }
    public string? CouncilBand { get; set; }
    public int ReducedConfidence { get; set; }
    public string? LintWarnings { get; set; }
    // M3b: the council run that produced this word version. 'manual' for the hand-authored corpus;
    // the reviewer path does not change it in this milestone.
    public string Source { get; set; } = "manual";
    public int? CouncilRunId { get; set; }
    public long? StatusChangedAt { get; set; }
    public int? ApprovedVersion { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
