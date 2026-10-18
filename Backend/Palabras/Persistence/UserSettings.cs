namespace Palabras.Persistence;

internal sealed class UserSettings
{
    public int UserId { get; set; }
    public double DesiredRetention { get; set; } = 0.90;
    public int NewPerDay { get; set; } = 12;
    public int MaxReviewsPerDay { get; set; } = 200;
    public int BacklogThreshold { get; set; } = 150;
    public int? SessionTargetS { get; set; } = 600;
    public int? SessionMaxS { get; set; } = 900;
    public int MaxIntervalDays { get; set; } = 730;
    public bool EnableFuzz { get; set; } = true;
    public string? LearningSteps { get; set; } = "[\"00:01:00\",\"00:10:00\"]";
    public string? RelearningSteps { get; set; } = "[\"00:10:00\"]";
    public int LeechLapses { get; set; } = 8;
    public string? WeightsJson { get; set; }
    public int WeightsVersion { get; set; }
}
