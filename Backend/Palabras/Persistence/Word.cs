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
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
