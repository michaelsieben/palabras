using Palabras.Srs;

namespace Palabras.Persistence;

internal sealed class Card
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SenseId { get; set; }
    public string Direction { get; set; } = "es_en";
    public int Epoch { get; set; }
    public CardState State { get; set; }
    public int? Step { get; set; }
    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public long DueMs { get; set; }
    public long? LastReviewMs { get; set; }
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public string? SuspendedReason { get; set; }
    public string? BuriedUntilLocalDate { get; set; }
    public bool IsLeech { get; set; }
    public string? IntroducedLocalDate { get; set; }
    public long CreatedAt { get; set; }
}
