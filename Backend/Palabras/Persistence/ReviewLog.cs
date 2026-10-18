using Palabras.Srs;

namespace Palabras.Persistence;

internal sealed class ReviewLog
{
    public int Id { get; set; }
    public string ClientAttemptId { get; set; } = "";
    public int UserId { get; set; }
    public int WordId { get; set; }
    public int CardId { get; set; }
    public int CardEpoch { get; set; }
    public string? SessionId { get; set; }
    public Rating Rating { get; set; }
    public long ReviewedAtMs { get; set; }
    public long ReceivedAtMs { get; set; }
    public double ElapsedDays { get; set; }
    public double? IntervalBeforeDays { get; set; }
    public double IntervalAfterDays { get; set; }
    public int? DurationMs { get; set; }
    public string Mode { get; set; } = "recognition";
    public bool? IsCorrectMeaning { get; set; }
    public bool? IsCorrectForm { get; set; }
    public string? FeedbackKind { get; set; }
    public bool Overridden { get; set; }
    public string? AnswerText { get; set; }
    public CardState StateBefore { get; set; }
    public double? StabilityBefore { get; set; }
    public double? DifficultyBefore { get; set; }
    public CardState StateAfter { get; set; }
    public double? StabilityAfter { get; set; }
    public double? DifficultyAfter { get; set; }
    public long DueAfterMs { get; set; }
    public int WeightsVersion { get; set; }
    public bool? CountsForSrs { get; set; } = true;
}
