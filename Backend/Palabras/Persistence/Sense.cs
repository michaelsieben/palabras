namespace Palabras.Persistence;

internal sealed class Sense
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public int SenseOrder { get; set; }
    public string EnGloss { get; set; } = "";
    public string EnAccepted { get; set; } = "[]";
    public string EsAccepted { get; set; } = "[]";
    public string ExampleEs { get; set; } = "";
    public string ExampleEn { get; set; } = "";
    public bool AccentsAreThePoint { get; set; }
    public string? Notes { get; set; }
}
