using FsrsSharp.Configuration;
using FsrsSharp.Core;
using FsrsSharp.Models;

namespace Palabras.Srs;

internal sealed class FsrsSharpScheduler : IScheduler
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<SchedulerOptions, Scheduler> _schedulers = new(new OptionsComparer());

    public ReviewOutcome Review(CardMemory card, Rating rating, DateTimeOffset reviewedAtUtc, SchedulerOptions options)
    {
        reviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        Card reviewed = GetScheduler(options).ReviewCard(ToCard(card), (FsrsSharp.Models.Rating)rating, reviewedAtUtc).Card;
        CardMemory after = FromCard(reviewed);
        return new ReviewOutcome(
            card, after,
            card.LastReviewUtc is { } lastReview ? (reviewedAtUtc - lastReview).TotalDays : 0,
            card.State != CardState.New && card.LastReviewUtc is { } previousReview
                ? (card.DueUtc - previousReview).TotalDays : null,
            (after.DueUtc - reviewedAtUtc).TotalDays,
            card.State == CardState.Review && rating == Rating.Again);
    }

    public double Retrievability(CardMemory card, DateTimeOffset atUtc, SchedulerOptions options)
    {
        return GetScheduler(options).GetCardRetrievability(ToCard(card), atUtc.ToUniversalTime());
    }

    private Scheduler GetScheduler(SchedulerOptions options)
    {
        // Snapshot collections: record equality alone compares list references, and callers
        // may retain mutable arrays. Neither may change the meaning of a cached scheduler.
        SchedulerOptions snapshot = options with
        {
            LearningSteps = options.LearningSteps.ToArray(),
            RelearningSteps = options.RelearningSteps.ToArray(),
            Weights = options.Weights?.ToArray()
        };
        lock (_cacheLock)
        {
            if (!_schedulers.TryGetValue(snapshot, out Scheduler? scheduler))
            {
                scheduler = new Scheduler(new FsrsConfig
                {
                    DesiredRetention = snapshot.DesiredRetention,
                    MaximumInterval = snapshot.MaximumIntervalDays,
                    EnableFuzzing = snapshot.EnableFuzz,
                    LearningSteps = snapshot.LearningSteps.ToArray(),
                    RelearningSteps = snapshot.RelearningSteps.ToArray(),
                    Parameters = snapshot.Weights is null
                        ? new FsrsParameters() : new FsrsParameters(snapshot.Weights.ToArray())
                });
                _schedulers.Add(snapshot, scheduler);
            }
            return scheduler;
        }
    }

    private static Card ToCard(CardMemory memory)
    {
        // Explicit id and due prevent Card's convenience defaults from reading a clock
        // or generating an id. Fsrs.Sharp supports New directly (unlike py-fsrs).
        return new Card(Guid.Empty, (State)memory.State, memory.Step,
            memory.Stability, memory.Difficulty, memory.DueUtc.ToUniversalTime(),
            memory.LastReviewUtc?.ToUniversalTime());
    }

    private static CardMemory FromCard(Card card)
    {
        return new CardMemory((CardState)card.State, card.Step, card.Stability,
            card.Difficulty, card.Due.ToUniversalTime(), card.LastReview?.ToUniversalTime());
    }

    private sealed class OptionsComparer : IEqualityComparer<SchedulerOptions>
    {
        public bool Equals(SchedulerOptions? x, SchedulerOptions? y)
        {
            return ReferenceEquals(x, y) || (x is not null && y is not null
                && x.DesiredRetention == y.DesiredRetention
                && x.MaximumIntervalDays == y.MaximumIntervalDays
                && x.EnableFuzz == y.EnableFuzz && x.WeightsVersion == y.WeightsVersion
                && x.LearningSteps.SequenceEqual(y.LearningSteps)
                && x.RelearningSteps.SequenceEqual(y.RelearningSteps)
                && (x.Weights is null ? y.Weights is null
                    : y.Weights is not null && x.Weights.SequenceEqual(y.Weights)));
        }

        public int GetHashCode(SchedulerOptions options)
        {
            var hash = new HashCode();
            hash.Add(options.DesiredRetention);
            hash.Add(options.MaximumIntervalDays);
            hash.Add(options.EnableFuzz);
            hash.Add(options.WeightsVersion);
            foreach (TimeSpan step in options.LearningSteps) { hash.Add(step); }
            foreach (TimeSpan step in options.RelearningSteps) { hash.Add(step); }
            hash.Add(options.Weights is null);
            if (options.Weights is not null)
            {
                foreach (double weight in options.Weights) { hash.Add(weight); }
            }
            return hash.ToHashCode();
        }
    }
}
