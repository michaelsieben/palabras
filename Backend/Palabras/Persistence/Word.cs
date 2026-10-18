namespace Palabras.Persistence;

internal sealed class Word
{
    // Future review/SRS relations reference this surrogate key, never Headword or username.
    public int Id { get; set; }
    public string ExternalId { get; set; } = "";
    public string Headword { get; set; } = "";
    public string? Article { get; set; }
    public string? Gender { get; set; }
    public string PartOfSpeech { get; set; } = "";
    public string Cefr { get; set; } = "";
    public string RegionVariant { get; set; } = "";
    public string RegionScope { get; set; } = "";
    public string? RegionNotes { get; set; }
    public string Register { get; set; } = "";
    public string? RegisterNotes { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    // Retains every schema field, including provenance, review, author notes and lint metadata.
    public string ContentJson { get; set; } = "";
    public List<Sense> Senses { get; set; } = [];
    public List<Alternative> Alternatives { get; set; } = [];
    public List<WordTopic> Topics { get; set; } = [];
}

internal sealed class Sense
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public int Position { get; set; }
    public string Gloss { get; set; } = "";
    public string Example { get; set; } = "";
    public string ExampleGloss { get; set; } = "";
    public string? Register { get; set; }
    // Schema's sense.notes includes variant-specific notes; no invented regional field.
    public string? Notes { get; set; }
}

internal sealed class Alternative
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public int Position { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Gloss { get; set; }
    public string Reason { get; set; } = "";
    public string? TargetId { get; set; }
    public string? Example { get; set; }
}

internal sealed class WordTopic
{
    public int WordId { get; set; }
    public string Topic { get; set; } = "";
}
