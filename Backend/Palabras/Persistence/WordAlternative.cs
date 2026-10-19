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
    // M3b alternatives v2 (design-amendment.md ΔE.3). Legacy rows keep field=NULL and word_version=0;
    // the reviewer chip resolver falls back to kind when field is NULL, so there is no SQL backfill.
    public string? Field { get; set; }
    public int Support { get; set; }
    public string? Models { get; set; }
    public string? Region { get; set; }
    public string? Register { get; set; }
    public int WordVersion { get; set; }
    public string Source { get; set; } = "manual";
}
