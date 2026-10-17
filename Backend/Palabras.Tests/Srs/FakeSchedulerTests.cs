using Palabras.Srs;
using Xunit;

namespace Palabras.Tests.Srs;

public sealed class FakeSchedulerTests
{
    [Theory]
    [InlineData(1, 0, 60)]
    [InlineData(2, 3, 600)]
    [InlineData(3, 0, 86400)]
    [InlineData(3, 3, 691200)]
    [InlineData(4, 0, 345600)]
    [InlineData(4, 3, 2764800)]
    public void FixedIntervalsAreDeterministic(int rating, int reps, int seconds)
    {
        var scheduler = new FakeScheduler(reps);
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var card = new CardMemory(CardState.Review, null, 10, 5, at, at.AddDays(-2));
        var options = new SchedulerOptions(0.9, 730, false,
            new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10) },
            new[] { TimeSpan.FromMinutes(10) }, null, 0);
        ReviewOutcome outcome = scheduler.Review(card, (Rating)rating, at, options);
        Assert.Equal(outcome, scheduler.Review(card, (Rating)rating, at, options));
        Assert.Equal(at.AddSeconds(seconds), outcome.After.DueUtc);
        Assert.Equal(5, outcome.After.Stability);
        Assert.Equal(5, outcome.After.Difficulty);
        Assert.Equal(rating == 1, outcome.IsLapse);
        Assert.Equal(2, outcome.ElapsedDaysAtReview);
        Assert.Equal(2, outcome.IntervalBeforeDays);
        Assert.Equal(0.9, scheduler.Retrievability(card, at, options));
    }
}
