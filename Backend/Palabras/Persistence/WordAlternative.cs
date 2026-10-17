namespace Palabras.Persistence;

internal sealed class WordAlternative
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Gloss { get; set; }
    public string Reason { get; set; } = "";
    public string? TargetKey { get; set; }
    public string? Example { get; set; }
    public int Rank { get; set; }
}
