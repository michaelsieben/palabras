namespace Palabras.Srs;

internal enum CardState { New = 0, Learning = 1, Review = 2, Relearning = 3 }
internal enum Rating { Again = 1, Hard = 2, Good = 3, Easy = 4 }

internal sealed record CardMemory(
    CardState State, int? Step, double? Stability, double? Difficulty,
    DateTimeOffset DueUtc, DateTimeOffset? LastReviewUtc);

internal sealed record SchedulerOptions(
    double DesiredRetention, int MaximumIntervalDays, bool EnableFuzz,
    IReadOnlyList<TimeSpan> LearningSteps, IReadOnlyList<TimeSpan> RelearningSteps,
    IReadOnlyList<double>? Weights, int WeightsVersion);

internal sealed record ReviewOutcome(
    CardMemory Before, CardMemory After,
    double ElapsedDaysAtReview, double? IntervalBeforeDays, double IntervalAfterDays, bool IsLapse);

internal interface IScheduler
{
    ReviewOutcome Review(CardMemory card, Rating rating, DateTimeOffset reviewedAtUtc, SchedulerOptions options);
    double Retrievability(CardMemory card, DateTimeOffset atUtc, SchedulerOptions options);
}
