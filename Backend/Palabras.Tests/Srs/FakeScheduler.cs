using Palabras.Srs;

namespace Palabras.Tests.Srs;

internal sealed class FakeScheduler : IScheduler
{
    // Reps are deliberately outside CardMemory. Tests choose the persisted count
    // explicitly rather than hiding a mutable per-card counter in this pure seam.
    private readonly int _reps;

    public FakeScheduler(int reps = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reps);
        _reps = reps;
    }

    public ReviewOutcome Review(CardMemory card, Rating rating, DateTimeOffset reviewedAtUtc, SchedulerOptions options)
    {
        TimeSpan interval = rating switch
        {
            Rating.Again => TimeSpan.FromMinutes(1),
            Rating.Hard => TimeSpan.FromMinutes(10),
            Rating.Good => TimeSpan.FromDays(Math.Pow(2, _reps)),
            Rating.Easy => TimeSpan.FromDays(4 * Math.Pow(2, _reps)),
            _ => throw new ArgumentOutOfRangeException(nameof(rating))
        };
        bool isLapse = card.State == CardState.Review && rating == Rating.Again;
        CardState state = isLapse ? CardState.Relearning
            : rating is Rating.Again or Rating.Hard ? CardState.Learning : CardState.Review;
        reviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        var after = new CardMemory(state, state == CardState.Review ? null : 0,
            5, 5, reviewedAtUtc + interval, reviewedAtUtc);
        return new ReviewOutcome(card, after,
            card.LastReviewUtc is { } last ? (reviewedAtUtc - last).TotalDays : 0,
            card.State != CardState.New && card.LastReviewUtc is { } previous
                ? (card.DueUtc - previous).TotalDays : null,
            interval.TotalDays, isLapse);
    }

    public double Retrievability(CardMemory card, DateTimeOffset atUtc, SchedulerOptions options) => 0.9;
}
