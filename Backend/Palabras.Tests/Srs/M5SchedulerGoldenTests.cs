using System.Globalization;
using System.Text.Json;
using FsrsSharp.Configuration;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests.Srs;

public sealed class M5SchedulerGoldenTests
{
    private static readonly JsonElement Golden = LoadGolden();
    private readonly FsrsSharpScheduler _scheduler = new();
    private readonly SchedulerOptions _options = ReadOptions();

    public static IEnumerable<object[]> SequenceIds()
    {
        foreach (JsonElement sequence in Golden.GetProperty("sequences").EnumerateArray())
        {
            yield return new object[] { sequence.GetProperty("id").GetInt32() };
        }
    }

    [Theory]
    [MemberData(nameof(SequenceIds))]
    public void ReplaysEveryOracleReview(int sequenceId)
    {
        JsonElement sequence = Golden.GetProperty("sequences")[sequenceId - 1];
        CardMemory memory = ReadMemory(sequence.GetProperty("initial")) with { State = CardState.New, Step = null };
        int reviewIndex = 0;
        int lapses = 0;
        foreach (JsonElement step in sequence.GetProperty("steps").EnumerateArray())
        {
            DateTimeOffset at = ReadTime(step.GetProperty("review_time"));
            Rating rating = (Rating)step.GetProperty("rating").GetInt32();
            ReviewOutcome outcome = _scheduler.Review(memory, rating, at, _options);
            CardMemory expected = ReadMemory(step.GetProperty("expected"));
            string context = $"sequence {sequenceId}, review {++reviewIndex}";

            EqualField(expected.State, outcome.After.State, context, "state");
            EqualField(expected.Step, outcome.After.Step, context, "step");
            Near(expected.Stability!.Value, outcome.After.Stability, context, "stability");
            Near(expected.Difficulty!.Value, outcome.After.Difficulty, context, "difficulty");
            EqualField(expected.DueUtc, outcome.After.DueUtc, context, "due");
            EqualField(expected.LastReviewUtc, outcome.After.LastReviewUtc, context, "last_review");
            EqualField(memory, outcome.Before, context, "before (input must remain unchanged)");
            EqualField(memory.LastReviewUtc is { } last ? (at - last).TotalDays : 0,
                outcome.ElapsedDaysAtReview, context, "elapsed_days");
            EqualField(memory.State != CardState.New && memory.LastReviewUtc is { } previous
                ? (double?)(memory.DueUtc - previous).TotalDays : null,
                outcome.IntervalBeforeDays, context, "interval_before");
            EqualField((expected.DueUtc - at).TotalDays, outcome.IntervalAfterDays, context, "interval_after");
            Assert.InRange(outcome.IntervalAfterDays, 0, 730);

            bool expectedLapse = memory.State == CardState.Review && rating == Rating.Again;
            EqualField(expectedLapse, outcome.IsLapse, context, "is_lapse");
            if (expectedLapse)
            {
                EqualField(CardState.Relearning, outcome.After.State, context, "lapse state");
                int previousLapses = lapses;
                // S6 owns the persisted counter; S5 supplies the exactly-once increment signal.
                lapses += outcome.IsLapse ? 1 : 0;
                EqualField(previousLapses + 1, lapses, context, "lapses increment signal");
            }
            memory = outcome.After;
        }
    }

    [Fact]
    public void FixtureHasLockedOracleAndAllReviews()
    {
        Assert.Equal("6.3.2", Golden.GetProperty("oracle").GetProperty("version").GetString());
        Assert.Equal(500, Golden.GetProperty("sequences").GetArrayLength());
        Assert.Equal(12571, Golden.GetProperty("sequences").EnumerateArray()
            .Sum(sequence => sequence.GetProperty("steps").GetArrayLength()));
        Assert.Equal(new FsrsParameters().Defaults, _options.Weights);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void FirstReviewPromotesNewWithEasyGraduatingImmediately(int rating, int expectedState)
    {
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var card = new CardMemory(CardState.New, null, null, null, at, null);
        ReviewOutcome outcome = _scheduler.Review(card, (Rating)rating, at, _options);
        Assert.Equal((CardState)expectedState, outcome.After.State);
        Assert.Null(outcome.IntervalBeforeDays);
        Assert.Equal(0, outcome.ElapsedDaysAtReview);
        Assert.False(outcome.IsLapse);
    }

    [Fact]
    public void RetrievabilityUsesPassedTimeAndWholeDays()
    {
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var card = new CardMemory(CardState.Review, null, 10, 5, at.AddDays(10), at);
        Assert.Equal(1, _scheduler.Retrievability(card, at, _options));
        Assert.Equal(_scheduler.Retrievability(card, at.AddDays(2), _options),
            _scheduler.Retrievability(card, at.AddDays(2.75), _options));
        Assert.True(_scheduler.Retrievability(card, at.AddDays(20), _options)
            < _scheduler.Retrievability(card, at.AddDays(2), _options));
        Assert.Equal(0, _scheduler.Retrievability(card with { LastReviewUtc = null }, at, _options));
    }

    [Fact]
    public void CacheSnapshotsOptionsAndHandlesConcurrentEqualValues()
    {
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var card = new CardMemory(CardState.New, null, null, null, at, null);
        TimeSpan[] steps = _options.LearningSteps.ToArray();
        SchedulerOptions mutable = _options with { LearningSteps = steps };
        ReviewOutcome original = _scheduler.Review(card, Rating.Again, at, mutable);
        steps[0] = TimeSpan.FromMinutes(2);
        Assert.Equal(at.AddMinutes(2), _scheduler.Review(card, Rating.Again, at, mutable).After.DueUtc);
        Parallel.For(0, 100, _ =>
        {
            SchedulerOptions equivalent = _options with
            {
                LearningSteps = _options.LearningSteps.ToArray(),
                RelearningSteps = _options.RelearningSteps.ToArray(),
                Weights = _options.Weights!.ToArray()
            };
            Assert.Equal(original, _scheduler.Review(card, Rating.Again, at, equivalent));
        });
        Assert.Equal(original, _scheduler.Review(card, Rating.Again, at, _options with { Weights = null }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void InvalidRatingsAreRejected(int rating)
    {
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var card = new CardMemory(CardState.New, null, null, null, at, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => _scheduler.Review(card, (Rating)rating, at, _options));
    }

    private static JsonElement LoadGolden()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Srs", "golden.json")));
        return document.RootElement.Clone();
    }

    private static SchedulerOptions ReadOptions()
    {
        JsonElement options = Golden.GetProperty("options");
        return new SchedulerOptions(options.GetProperty("desired_retention").GetDouble(),
            options.GetProperty("maximum_interval_days").GetInt32(),
            options.GetProperty("enable_fuzzing").GetBoolean(),
            options.GetProperty("learning_steps_seconds").EnumerateArray().Select(x => TimeSpan.FromSeconds(x.GetDouble())).ToArray(),
            options.GetProperty("relearning_steps_seconds").EnumerateArray().Select(x => TimeSpan.FromSeconds(x.GetDouble())).ToArray(),
            options.GetProperty("parameters").EnumerateArray().Select(x => x.GetDouble()).ToArray(), 0);
    }

    private static CardMemory ReadMemory(JsonElement memory)
    {
        return new CardMemory((CardState)memory.GetProperty("state").GetInt32(),
            memory.GetProperty("step").ValueKind == JsonValueKind.Null ? null : memory.GetProperty("step").GetInt32(),
            NullableDouble(memory.GetProperty("stability")), NullableDouble(memory.GetProperty("difficulty")),
            ReadTime(memory.GetProperty("due")), memory.GetProperty("last_review").ValueKind == JsonValueKind.Null
                ? null : ReadTime(memory.GetProperty("last_review")));
    }

    private static double? NullableDouble(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();

    private static DateTimeOffset ReadTime(JsonElement value) => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    private static void EqualField<T>(T expected, T actual, string context, string field)
    {
        Assert.True(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{context}, {field}: expected {expected}, actual {actual}");
    }

    private static void Near(double expected, double? actual, string context, string field)
    {
        Assert.True(actual.HasValue && double.IsFinite(actual.Value) && Math.Abs(expected - actual.Value) <= 1e-6,
            $"{context}, {field}: expected {expected:R}, actual {actual:R}");
    }
}
