using System.Globalization;
using Palabras.Learn;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 U6 planner (plan §2.4): "should this learner get a reminder today, and if not, why" as a pure
// function. The fixtures are local instants in Madrid (+02:00 while CEST is in effect), because the
// reminder time is a wall-clock time in the learner's zone and the DST cases must not be guessed at.
public sealed class M8ReminderPlannerTests
{
    private static readonly ReminderPreferences Defaults = new(true, "22:00", null, 90);

    [Fact]
    public void TheReminderTimeItselfIsInsideTheWindow()
    {
        var plan = ReminderPlanner.Evaluate(Defaults, todayStatus: null, Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(ReminderAction.Send, plan.Action);
        Assert.Equal(ReminderPlanner.InWindowReason, plan.Reason);
        Assert.Equal(new DateOnly(2026, 10, 20), plan.LocalDate);
        // 22:00 + 90 minutes = 23:30 local, i.e. 21:30Z with the offset in effect that evening.
        Assert.Equal(Instant("2026-10-20T21:30:00Z"), plan.ExpiresAtMs);
        Assert.Equal(5400, plan.TtlSeconds);
    }

    [Fact]
    public void AMomentBeforeTheReminderTimeWaitsForTheNextTick()
    {
        var plan = ReminderPlanner.Evaluate(Defaults, null, Local("2026-10-20T21:59:00+02:00"));

        Assert.Equal(ReminderAction.Skip, plan.Action);
        Assert.Equal(ReminderPlanner.BeforeWindowReason, plan.Reason);
    }

    [Fact]
    public void TheLastSecondOfTheWindowStillSends()
    {
        var plan = ReminderPlanner.Evaluate(Defaults, null, Local("2026-10-20T23:29:59+02:00"));

        Assert.Equal(ReminderAction.Send, plan.Action);
        Assert.Equal(60, plan.TtlSeconds);
    }

    [Fact]
    public void TheWindowEndItselfIsAWindowMissNotASend()
    {
        var plan = ReminderPlanner.Evaluate(Defaults, null, Local("2026-10-20T23:30:00+02:00"));

        Assert.Equal(ReminderAction.WindowMissed, plan.Action);
        Assert.Equal(ReminderPlanner.WindowMissedReason, plan.Reason);
    }

    [Theory]
    [InlineData("2026-10-20T23:45:00+02:00")]
    [InlineData("2026-10-20T23:58:00+02:00")]
    public void ARestartAfterTheWindowRecordsTheMissInsteadOfASend(string now)
    {
        var plan = ReminderPlanner.Evaluate(Defaults, null, Local(now));

        Assert.Equal(ReminderAction.WindowMissed, plan.Action);
    }

    [Fact]
    public void AWindowThatWouldCrossMidnightEndsAtTheEndOfTheLocalDay()
    {
        // 23:00 + 240 minutes would be 03:00 tomorrow; the plan clips it at 23:59 so no reminder can
        // land in the middle of the night.
        var preferences = new ReminderPreferences(true, "23:00", null, 240);

        var inside = ReminderPlanner.Evaluate(preferences, null, Local("2026-10-20T23:58:00+02:00"));
        var after = ReminderPlanner.Evaluate(preferences, null, Local("2026-10-20T23:59:00+02:00"));

        Assert.Equal(ReminderAction.Send, inside.Action);
        Assert.Equal(Instant("2026-10-20T21:59:00Z"), inside.ExpiresAtMs);
        Assert.Equal(60, inside.TtlSeconds);
        Assert.Equal(ReminderAction.WindowMissed, after.Action);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ADisabledSettingSuppressesTheDay(bool enabled, bool expectedSend)
    {
        var plan = ReminderPlanner.Evaluate(Defaults with { Enabled = enabled }, null,
            Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(expectedSend ? ReminderAction.Send : ReminderAction.Skip, plan.Action);
        if (!expectedSend)
        {
            Assert.Equal(ReminderPlanner.DisabledReason, plan.Reason);
        }
    }

    [Theory]
    [InlineData("2026-10-20", true)]
    [InlineData("2026-10-21", true)]
    [InlineData("2026-10-19", false)]
    [InlineData(null, false)]
    public void AMuteIsInclusiveOfItsLastDay(string? mutedUntil, bool suppresses)
    {
        var plan = ReminderPlanner.Evaluate(Defaults with { MutedUntil = mutedUntil }, null,
            Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(suppresses ? ReminderAction.Skip : ReminderAction.Send, plan.Action);
        if (suppresses)
        {
            Assert.Equal(ReminderPlanner.MutedReason, plan.Reason);
        }
    }

    [Fact]
    public void AStoredMuteThatIsNotADateIsIgnoredRatherThanSilencingForever()
    {
        var plan = ReminderPlanner.Evaluate(Defaults with { MutedUntil = "not-a-date" }, null,
            Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(ReminderAction.Send, plan.Action);
    }

    [Theory]
    [InlineData(DayStatusValues.Done, nameof(ReminderAction.Skip), ReminderPlanner.DoneReason)]
    [InlineData(DayStatusValues.Skipped, nameof(ReminderAction.Skip), ReminderPlanner.SkippedReason)]
    [InlineData(DayStatusValues.Missed, nameof(ReminderAction.Send), ReminderPlanner.InWindowReason)]
    [InlineData(null, nameof(ReminderAction.Send), ReminderPlanner.InWindowReason)]
    public void OnlyDoneAndSkippedCloseTheDay(string? status, string expectedAction, string reason)
    {
        var plan = ReminderPlanner.Evaluate(Defaults, status, Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(expectedAction, plan.Action.ToString());
        Assert.Equal(reason, plan.Reason);
    }

    [Theory]
    [InlineData("22:60")]
    [InlineData("23:46")]
    [InlineData("7:00")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnusableStoredTimeIsRefusedInsteadOfThrowingInsideATick(string? time)
    {
        var preferences = new ReminderPreferences(true, time ?? "", null, 90);

        var plan = ReminderPlanner.Evaluate(preferences, null, Local("2026-10-20T22:00:00+02:00"));

        Assert.Equal(ReminderAction.Skip, plan.Action);
        Assert.Equal(ReminderPlanner.InvalidSettingsReason, plan.Reason);
        // The window still reports the default arithmetic, so a log line has a real end instant.
        Assert.Equal(Instant("2026-10-20T21:30:00Z"), plan.ExpiresAtMs);
    }

    [Theory]
    // Europe/Madrid springs forward on 2026-03-29 (02:00 CET -> 03:00 CEST) and falls back on
    // 2026-10-25 (03:00 CEST -> 02:00 CET). Both windows sit at 22:00, so each must end in the offset
    // that is in effect that evening, not the one from the previous day.
    [InlineData("2026-03-29T22:00:00+02:00", "2026-03-29T21:30:00Z")]
    [InlineData("2026-10-25T22:00:00+01:00", "2026-10-25T22:30:00Z")]
    public void TheWindowEndIsAnInstantInTheLearnerZoneOnBothDstDays(string now, string expectedExpiry)
    {
        var plan = ReminderPlanner.Evaluate(Defaults, null, Local(now));

        Assert.Equal(ReminderAction.Send, plan.Action);
        Assert.Equal(Instant(expectedExpiry), plan.ExpiresAtMs);
        Assert.Equal(5400, plan.TtlSeconds);
    }

    [Fact]
    public void TheTtlNeverDropsBelowThePlanFloor()
    {
        Assert.Equal(60, ReminderPlanner.TimeToLiveSeconds(Local("2026-10-20T23:29:30+02:00"),
            Local("2026-10-20T23:29:59+02:00")));
        Assert.Equal(5400, ReminderPlanner.TimeToLiveSeconds(Local("2026-10-20T23:30:00+02:00"),
            Local("2026-10-20T22:00:00+02:00")));
    }

    [Theory]
    // any sent wins, even when another device never landed.
    [InlineData(new[] { "sent", "transient" }, 2, "sent")]
    // every device gone: nothing left to retry tomorrow.
    [InlineData(new[] { "gone", "gone" }, 2, "gone")]
    // attempted but never delivered: a failure, not a miss.
    [InlineData(new[] { "transient", "rejected" }, 2, "failed")]
    // claimed and never attempted (crash between claim and first send).
    [InlineData(new[] { "pending" }, 0, "window_missed")]
    [InlineData(new string[0], 0, "window_missed")]
    public void TheFinalResultFollowsThePlanForEveryDeviceMix(string[] results, int attempts, string expected)
    {
        var deliveries = results
            .Select((result, index) => new ReminderDelivery(index + 1, index + 1, attempts, result))
            .ToList();

        Assert.Equal(expected, ReminderPlanner.FinalResult(deliveries));
    }

    [Fact]
    public void TheDeliveryResultNamesMatchTheFrozenCheckVocabulary()
    {
        // push_deliveries.result is a U1 CHECK; the sender writes it through the outcome classifier,
        // the reminder loop reads it back. Drift here would be a silent no-op, not a compile error.
        Assert.Equal(PushDeliveryResults.Sent, PushOutcomeClassifier.Record(PushOutcome.Delivered));
        Assert.Equal(PushDeliveryResults.Gone, PushOutcomeClassifier.Record(PushOutcome.Gone));
        Assert.Equal(PushDeliveryResults.Rejected, PushOutcomeClassifier.Record(PushOutcome.Rejected));
        Assert.Equal(PushDeliveryResults.Transient, PushOutcomeClassifier.Record(PushOutcome.Transient));
        Assert.NotEqual(PushDeliveryResults.Pending, PushDeliveryResults.Sent);
    }

    private static DateTimeOffset Local(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
}
