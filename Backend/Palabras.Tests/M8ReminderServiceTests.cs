using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Learn;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 U6 trigger (plan §2.4, §4 U6): the minute tick driven by a fake clock, against a migrated
// throwaway database and a scripted transport. Everything is asserted from the rows the tick writes
// — one decision per learner-day, one delivery row per device, the budget in `attempts` — because
// that is what makes "once" hold across restarts, not an in-memory flag.
public sealed class M8ReminderServiceTests
{
    [Fact]
    public async Task AReminderIsClaimedSentAndThenLeftAlone()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.SeedDayStatus("2026-10-19", DayStatusValues.Done);
        var service = harness.Service();

        var tick = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("sent", decision.Result);
        Assert.Equal("2026-10-20", decision.LocalDate);
        Assert.Equal("daily", decision.Kind);
        Assert.Equal("webpush", decision.Channel);
        Assert.Equal(5400, decision.TtlS);
        Assert.Equal(Instant("2026-10-20T21:30:00Z"), decision.ExpiresAtMs);
        Assert.True(decision.SkipAvailable);
        var delivery = Assert.Single(await harness.DeliveriesAsync());
        Assert.Equal(decision.Id, delivery.ReminderLogId);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal("sent", delivery.Result);
        Assert.Equal(201, delivery.LastStatus);

        var request = Assert.Single(harness.Transport.Requests);
        Assert.Equal("daily-2026-10-20", request.Topic);
        Assert.Equal(5400, request.TimeToLiveSeconds);
        Assert.Equal(PushUrgency.Normal, request.Urgency);
        using var payload = JsonDocument.Parse(request.Payload);
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("daily", root.GetProperty("kind").GetString());
        Assert.Equal("2026-10-20", root.GetProperty("localDate").GetString());
        Assert.Equal("2026-10-20T21:30:00Z", root.GetProperty("expiresAt").GetString());
        // Yesterday was done, so the body carries the streak the reminder is about.
        Assert.Equal(1, root.GetProperty("streak").GetInt32());
        Assert.True(root.GetProperty("skipAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("skipNextAvailable").ValueKind);
        Assert.Equal("/?start=1", root.GetProperty("url").GetString());
        // No identifier, username or endpoint may ever reach the payload (ΔH).
        Assert.DoesNotContain("learner", request.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fcm.googleapis.com", request.Payload, StringComparison.Ordinal);

        // The next tick has nothing left to decide for today.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 0, 0), second);
        Assert.Single(harness.Transport.Requests);
        Assert.Equal("sent", Assert.Single(await harness.DecisionsAsync()).Result);
    }

    [Fact]
    public async Task ARestartInsideTheWindowSendsTheDaysOnlyReminderOnce()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:05:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();

        // First start after 22:00: the immediate tick must send, not wait for tomorrow.
        Assert.Equal(new ReminderTick(1, 1, 1), await harness.Service().RunTickAsync(CancellationToken.None));
        // A restart a minute later is a second tick on the same claim: resumption, not a second send.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ReminderTick(1, 0, 0), await harness.Service().RunTickAsync(CancellationToken.None));

        Assert.Single(harness.Transport.Requests);
        Assert.Equal("sent", Assert.Single(await harness.DecisionsAsync()).Result);
        Assert.Single(await harness.DeliveriesAsync());
    }

    [Fact]
    public async Task ARestartAfterTheWindowRecordsAWindowMissAndSendsNothing()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T23:45:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DeliveriesAsync());
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("window_missed", decision.Result);
        Assert.Null(decision.TtlS);
        Assert.Null(decision.ExpiresAtMs);
        // The miss is recorded once, not re-decided every minute.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ReminderTick(1, 0, 0), await harness.Service().RunTickAsync(CancellationToken.None));
        Assert.Single(await harness.DecisionsAsync());
    }

    [Theory]
    [InlineData(DayStatusValues.Done)]
    [InlineData(DayStatusValues.Skipped)]
    public async Task ADayTheLearnerClosedIsNotRemindedAndWritesNoDecision(string status)
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.SeedDayStatus("2026-10-20", status);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 0, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DecisionsAsync());
    }

    [Fact]
    public async Task AMuteThroughTodaySuppressesTheDayWithoutADecisionRow()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.SeedSettings(mutedUntil: "2026-10-20");

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 0, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DecisionsAsync());
    }

    [Fact]
    public async Task ADisabledReminderSuppressesTheDayWithoutADecisionRow()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.SeedSettings(enabled: false);

        Assert.Equal(new ReminderTick(1, 0, 0), await harness.Service().RunTickAsync(CancellationToken.None));
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DecisionsAsync());
    }

    [Fact]
    public async Task ACustomTimeMovesTheWindowAndIsHonouredNotIgnored()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T21:30:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.SeedSettings(time: "21:30", window: 30);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        var request = Assert.Single(harness.Transport.Requests);
        Assert.Equal(1800, request.TimeToLiveSeconds);
        using var payload = JsonDocument.Parse(request.Payload);
        Assert.Equal("2026-10-20T20:00:00Z", payload.RootElement.GetProperty("expiresAt").GetString());
    }

    [Fact]
    public async Task AStoredTimeTheApiWouldRefuseCannotBreakTheTick()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        // The frozen CHECK only requires length 5, so a hand-edited row can hold this value.
        harness.SeedSettings(time: "22:60");

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 0, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DecisionsAsync());
        Assert.Contains(harness.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("not a valid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneDeadDeviceDoesNotStopTheOtherDevicesReminder()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        var phone = harness.SeedSubscription(endpoint: "https://fcm.googleapis.com/fcm/send/phone");
        var tablet = harness.SeedSubscription(endpoint: "https://fcm.googleapis.com/fcm/send/tablet");
        harness.Transport.Answer(503);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        Assert.Equal(2, harness.Transport.Requests.Count);
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("sent", decision.Result);
        var deliveries = await harness.DeliveriesAsync();
        Assert.Equal(2, deliveries.Count);
        var dead = Assert.Single(deliveries, row => row.SubscriptionId == phone);
        Assert.Equal("transient", dead.Result);
        Assert.Equal(1, dead.Attempts);
        Assert.Equal(503, dead.LastStatus);
        var alive = Assert.Single(deliveries, row => row.SubscriptionId == tablet);
        Assert.Equal("sent", alive.Result);
        Assert.Equal(201, alive.LastStatus);
    }

    [Fact]
    public async Task TheRetryBudgetIsThreeAttemptsPerDeviceAndTheDayClosesAsFailed()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.Transport.Answer(503).Answer(503).Answer(503);
        var service = harness.Service();

        for (var minute = 0; minute < 4; minute++)
        {
            if (minute > 0)
            {
                harness.Time.Advance(TimeSpan.FromMinutes(1));
            }
            await service.RunTickAsync(CancellationToken.None);
        }

        // Three attempts, then the budget is spent: the fourth tick makes no network call.
        Assert.Equal(3, harness.Transport.Requests.Count);
        Assert.Equal("sending", Assert.Single(await harness.DecisionsAsync()).Result);
        Assert.Equal(3, Assert.Single(await harness.DeliveriesAsync()).Attempts);

        // The window closes with nothing delivered: the day is `failed`, not a silent `sending`.
        harness.Time.Advance(Local("2026-10-20T23:31:00+02:00") - harness.Time.GetUtcNow());
        await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(3, harness.Transport.Requests.Count);
        Assert.Equal("failed", Assert.Single(await harness.DecisionsAsync()).Result);
    }

    [Fact]
    public async Task FinishingTheDayMidWindowStopsTheRetries()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();
        harness.Transport.Answer(503);
        var service = harness.Service();

        await service.RunTickAsync(CancellationToken.None);
        Assert.Single(harness.Transport.Requests);
        Assert.Equal("sending", Assert.Single(await harness.DecisionsAsync()).Result);

        // 22:00:30: the learner finishes the session. The next tick must not retry the device.
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        harness.SeedDayStatus("2026-10-20", DayStatusValues.Done);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Transport.Requests);
        var decision = Assert.Single(await harness.DecisionsAsync());
        // The one attempt did fail, and the frozen CHECK has no "cancelled": `failed` is the honest
        // value, and the important assertion is that the day left `sending` and no retry happened.
        Assert.NotEqual("sending", decision.Result);
        Assert.Equal("failed", decision.Result);
    }

    [Fact]
    public async Task AClaimWrittenBeforeACrashIsResumedWithTheSameTopic()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:03:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        // The process died between the commit of the claim and the first send.
        harness.SeedClaim("2026-10-20", subscriptionId);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        var request = Assert.Single(harness.Transport.Requests);
        Assert.Equal("daily-2026-10-20", request.Topic);
        Assert.Equal("sent", Assert.Single(await harness.DecisionsAsync()).Result);
        var delivery = Assert.Single(await harness.DeliveriesAsync());
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal("sent", delivery.Result);
    }

    [Fact]
    public async Task AClaimWhoseProcessDiedBeforeTheWindowClosedIsFinalisedAsWindowMissed()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T23:45:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        // The process died between the commit of the claim and the first send, and came back after
        // windowEnd: nothing was attempted, so the only honest result is `window_missed`.
        harness.SeedClaim("2026-10-20", subscriptionId);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("window_missed", decision.Result);
        // Resuming the claim finalises it through the send loop, which recomputes the TTL from the
        // plan (floor of 60 s once the window has closed); the assertion that matters is the result.
        Assert.Equal(60, decision.TtlS);
        Assert.Equal(Instant("2026-10-20T21:30:00Z"), decision.ExpiresAtMs);
        var delivery = Assert.Single(await harness.DeliveriesAsync());
        Assert.Equal("pending", delivery.Result);
        Assert.Equal(0, delivery.Attempts);
    }

    [Fact]
    public async Task AClaimWithAttemptsWhoseProcessDiedBeforeTheWindowClosedIsFinalisedAsFailed()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T23:45:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        // One attempt was made and classified transient before the process died: `failed` is the
        // honest result, never a silent `sending` and never `window_missed`.
        harness.SeedClaim("2026-10-20", subscriptionId, deliveryResult: "transient", attempts: 1);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Equal("failed", Assert.Single(await harness.DecisionsAsync()).Result);
        var delivery = Assert.Single(await harness.DeliveriesAsync());
        Assert.Equal("transient", delivery.Result);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task AClaimFromThePreviousLocalDayIsFinalisedByTheNextDaysFirstTick()
    {
        // 00:05 on the 21st: today's window has not opened, and yesterday's claim is still `sending`
        // because the tick only ever looks up today's date.
        using var harness = new ReminderHarness(Local("2026-10-21T00:05:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        harness.SeedClaim("2026-10-20", subscriptionId);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        // The sweep is not today's decision: nothing is recorded for the 21st and no push is tried.
        Assert.Equal(new ReminderTick(1, 0, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("2026-10-20", decision.LocalDate);
        Assert.Equal("window_missed", decision.Result);
        Assert.Contains(harness.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("outlived its local day", StringComparison.Ordinal));

        // Swept once: the next tick finds nothing left to finalise and decides nothing.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ReminderTick(1, 0, 0), await harness.Service().RunTickAsync(CancellationToken.None));
        Assert.Equal("window_missed", Assert.Single(await harness.DecisionsAsync()).Result);
    }

    [Fact]
    public async Task AClaimFromAnEarlierDayWithAttemptsIsSweptAsFailedAndTodaysClaimIsResumed()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:03:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        // The host was down for the whole 19th after one attempt, and today's claim is waiting.
        harness.SeedClaim("2026-10-19", subscriptionId, deliveryResult: "transient", attempts: 1);
        harness.SeedClaim("2026-10-20", subscriptionId);

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        Assert.Equal("daily-2026-10-20", Assert.Single(harness.Transport.Requests).Topic);
        var decisions = await harness.DecisionsAsync();
        Assert.Equal("failed", Assert.Single(decisions, row => row.LocalDate == "2026-10-19").Result);
        Assert.Equal("sent", Assert.Single(decisions, row => row.LocalDate == "2026-10-20").Result);
    }

    [Fact]
    public async Task ATickThatStartsBeforeLocalMidnightKeepsOneDayForItsWholeDecision()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:05:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        harness.SeedClaim("2026-10-20", subscriptionId);
        // The first read is 22:05 on the 20th and every later read is 00:05 on the 21st, so a tick
        // that reads the clock more than once plans one day and looks up another.
        var clock = new FlippingTimeProvider(Local("2026-10-20T22:05:00+02:00"),
            Local("2026-10-21T00:05:00+02:00"));

        var tick = await harness.Service(time: clock).RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), tick);
        var decision = Assert.Single(await harness.DecisionsAsync());
        Assert.Equal("2026-10-20", decision.LocalDate);
        Assert.Equal("sent", decision.Result);
        Assert.Equal("daily-2026-10-20", Assert.Single(harness.Transport.Requests).Topic);
    }

    [Fact]
    public async Task ADeviceBoundToAnotherKeyIsRecordedAsNoTargetAndNeverSentTo()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription(keyId: "0000000000000000");

        var tick = await harness.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 0), tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DeliveriesAsync());
        Assert.Equal("no_target", Assert.Single(await harness.DecisionsAsync()).Result);
        // The row is left untouched: only the browser re-subscribing can heal it.
        var subscription = await harness.SubscriptionAsync(subscriptionId);
        Assert.Equal(PushSubscriptionLifecycle.Active, subscription.State);
    }

    [Fact]
    public async Task NothingIsSentOrDecidedWhilePushIsDisabled()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        harness.SeedSubscription();

        var tick = await harness.Service(pushEnabled: false).RunTickAsync(CancellationToken.None);

        Assert.Equal(ReminderTick.None, tick);
        Assert.Empty(harness.Transport.Requests);
        Assert.Empty(await harness.DecisionsAsync());
        Assert.Empty(await harness.DeliveriesAsync());
    }

    [Fact]
    public async Task ABrokenKeyFileMakesTheTickANoOpInsteadOfWritingUndeliverableDecisions()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"),
            keyFileContent: "{ not a key file");
        await harness.InitializeAsync();
        harness.SeedSubscription(keyId: "0000000000000000");

        Assert.Equal(ReminderTick.None, await harness.Service().RunTickAsync(CancellationToken.None));
        Assert.Empty(await harness.DecisionsAsync());
        Assert.Empty(await harness.DeliveriesAsync());
    }

    [Fact]
    public async Task TheLearnersZoneDecidesWhichLocalDayIsReminded()
    {
        var instant = Local("2026-10-20T09:00:00Z");
        // 09:00Z is 11:00 in Madrid but 22:00 in Auckland (NZDT, +13:00): the same instant, two
        // different answers, and only the learner's stored zone may choose between them.
        using var auckland = new ReminderHarness(instant, timezone: "Pacific/Auckland");
        await auckland.InitializeAsync();
        auckland.SeedSubscription();

        var aucklandTick = await auckland.Service().RunTickAsync(CancellationToken.None);

        Assert.Equal(new ReminderTick(1, 1, 1), aucklandTick);
        Assert.Equal("2026-10-20", Assert.Single(await auckland.DecisionsAsync()).LocalDate);
        var request = Assert.Single(auckland.Transport.Requests);
        Assert.Equal(5400, request.TimeToLiveSeconds);
        using (var payload = JsonDocument.Parse(request.Payload))
        {
            // 23:30 in Auckland is 10:30Z the same day (NZDT, +13:00).
            Assert.Equal("2026-10-20T10:30:00Z", payload.RootElement.GetProperty("expiresAt").GetString());
        }

        using var madrid = new ReminderHarness(instant);
        await madrid.InitializeAsync();
        madrid.SeedSubscription();

        Assert.Equal(new ReminderTick(1, 0, 0), await madrid.Service().RunTickAsync(CancellationToken.None));
        Assert.Empty(madrid.Transport.Requests);
        Assert.Empty(await madrid.DecisionsAsync());
    }

    [Fact]
    public async Task TheRolloverGateDoesNotChangeTheReminderDecision()
    {
        var now = Local("2026-10-20T22:00:00+02:00");

        // Gate OFF: no missed-day history exists and the tick sends.
        using var off = new ReminderHarness(now);
        await off.InitializeAsync();
        off.SeedSubscription();
        var offTick = await off.Service().RunTickAsync(CancellationToken.None);
        var offPayload = Assert.Single(off.Transport.Requests).Payload;

        // Gate ON: the rollover records yesterday as missed first; today's reminder is unchanged.
        using var on = new ReminderHarness(now);
        await on.InitializeAsync();
        on.SeedSubscription();
        var written = await DayRollover.RunAsync(on.Database, on.Time, NullLogger.Instance,
            now.AddDays(-30), CancellationToken.None);
        var onTick = await on.Service().RunTickAsync(CancellationToken.None);
        var onPayload = Assert.Single(on.Transport.Requests).Payload;

        Assert.Equal(1, written);
        Assert.Equal(offTick, onTick);
        Assert.Equal(new ReminderTick(1, 1, 1), onTick);
        Assert.Equal(offPayload, onPayload);
        Assert.Equal("sent", Assert.Single(await on.DecisionsAsync()).Result);
    }

    [Fact]
    public async Task ConcurrentClaimsWriteOneDecisionAndOneDeliverySet()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        var today = new DateOnly(2026, 10, 20);
        var nowMs = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var writer = harness.Writer();

        var claims = await Task.WhenAll(
            writer.ClaimAsync(1, today, true, 5400, nowMs + 5_400_000, [subscriptionId], nowMs,
                CancellationToken.None),
            writer.ClaimAsync(1, today, true, 5400, nowMs + 5_400_000, [subscriptionId], nowMs,
                CancellationToken.None));

        Assert.Single(claims, claim => claim.Claimed);
        Assert.Equal(claims[0].Row.Id, claims[1].Row.Id);
        Assert.Single(await harness.DecisionsAsync());
        Assert.Single(await harness.DeliveriesAsync());
    }

    [Fact]
    public async Task AClaimThatMeetsAWriteLockIsRetriedInsteadOfSkipped()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        var today = new DateOnly(2026, 10, 20);
        var nowMs = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();

        // Another writer holds the single SQLite write lock. The writer under test opens connections
        // with a zero busy timeout, so the first attempt is a deterministic SQLITE_BUSY, and the
        // wait hook releases the lock instead of sleeping for it.
        using var lockConnection = new SqliteConnection(ConnectionString(harness.DatabasePath));
        await lockConnection.OpenAsync(CancellationToken.None);
        using var lockTransaction = lockConnection.BeginTransaction(deferred: false);
        var released = false;
        var waits = 0;
        var writer = new ReminderClaimWriter(() => OpenWithoutWaiting(harness.DatabasePath),
            new ListLogger<ReminderClaimWriter>(harness.LogSink), (attempt, cancellationToken) =>
            {
                waits++;
                if (!released)
                {
                    released = true;
                    lockTransaction.Rollback();
                }
                return Task.CompletedTask;
            });

        var claim = await writer.ClaimAsync(1, today, true, 5400, nowMs + 5_400_000, [subscriptionId],
            nowMs, CancellationToken.None);

        Assert.True(claim.Claimed);
        Assert.Equal(1, waits);
        Assert.Single(await harness.DecisionsAsync());
        Assert.Single(await harness.DeliveriesAsync());
        Assert.Contains(harness.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("SQLite was busy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteLockThatNeverClearsFailsAfterTheBoundedRetriesAndWritesNothing()
    {
        using var harness = new ReminderHarness(Local("2026-10-20T22:00:00+02:00"));
        await harness.InitializeAsync();
        var subscriptionId = harness.SeedSubscription();
        var today = new DateOnly(2026, 10, 20);
        var nowMs = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();

        using var lockConnection = new SqliteConnection(ConnectionString(harness.DatabasePath));
        await lockConnection.OpenAsync(CancellationToken.None);
        using var lockTransaction = lockConnection.BeginTransaction(deferred: false);
        var waits = 0;
        var writer = new ReminderClaimWriter(() => OpenWithoutWaiting(harness.DatabasePath),
            new ListLogger<ReminderClaimWriter>(harness.LogSink), (attempt, cancellationToken) =>
            {
                waits++;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<SqliteException>(() => writer.ClaimAsync(1, today, true, 5400,
            nowMs + 5_400_000, [subscriptionId], nowMs, CancellationToken.None));

        Assert.Equal(ReminderClaimWriter.MaximumBusyAttempts - 1, waits);
        Assert.Empty(await harness.DecisionsAsync());
        lockTransaction.Rollback();
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = false,
        // One second, not zero: ADO.NET reads a zero command timeout as "no limit", which would turn
        // a held write lock into a hung test instead of a SQLITE_BUSY the writer can retry.
        DefaultTimeout = 1
    }.ToString();

    private static PalabrasDbContext OpenWithoutWaiting(string path) =>
        new(new DbContextOptionsBuilder<PalabrasDbContext>().UseSqlite(ConnectionString(path)).Options);

    private static DateTimeOffset Local(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    // One throwaway database, one VAPID key file and one recorded log per test.
    private sealed class ReminderHarness : IDisposable
    {
        private readonly string _root =
            Path.Combine("/tmp", "palabras-push", "reminder-" + Guid.NewGuid().ToString("N"));
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        internal ReminderHarness(DateTimeOffset now, string timezone = "Europe/Madrid",
            string? keyFileContent = null)
        {
            Time = new FakeTimeProvider(now);
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "palabras.db");
            Database = new DatabaseSettings(DatabasePath);
            VapidPath = Path.Combine(_root, "vapid.json");
            if (keyFileContent is not null)
            {
                File.WriteAllText(VapidPath, keyFileContent);
            }
            PushSettings = new PushSettings(true, VapidPath, PushSettings.DefaultSubject);
            Vapid = new VapidKeyStore(PushSettings, Time, NullLogger<VapidKeyStore>.Instance);
            using var database = Database.CreateContext();
            database.Database.Migrate();
            var stamp = DateTimeOffset.UnixEpoch;
            database.Users.Add(new User
            {
                Id = 1,
                Username = "learner",
                Role = "owner",
                PasswordHash = "hash",
                Timezone = timezone,
                CreatedAt = stamp,
                UpdatedAt = stamp
            });
            database.SaveChanges();
        }

        internal FakeTimeProvider Time { get; }

        internal string DatabasePath { get; }

        internal string VapidPath { get; }

        internal DatabaseSettings Database { get; }

        internal PushSettings PushSettings { get; }

        internal VapidKeyStore Vapid { get; }

        internal ScriptedPushTransport Transport { get; } = new();

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        internal Action<(LogLevel Level, string Message)> LogSink => _entries.Add;

        internal string KeyId => Vapid.Keys!.KeyId;

        internal Task InitializeAsync() => Vapid.InitializeAsync(CancellationToken.None);

        internal ReminderClaimWriter Writer() => new(Database, new ListLogger<ReminderClaimWriter>(_entries.Add));

        internal ReminderService Service(bool pushEnabled = true, TimeProvider? time = null) => new(Database,
            PushSettings with { Enabled = pushEnabled }, Vapid, Writer(),
            new PushSender(Database, Transport, Time, new ListLogger<PushSender>(_entries.Add)),
            time ?? Time, NullLoggerFactory.Instance, new ListLogger<ReminderService>(_entries.Add));

        internal int SeedSubscription(string? keyId = null,
            string endpoint = "https://fcm.googleapis.com/fcm/send/device-a",
            string state = PushSubscriptionLifecycle.Active)
        {
            using var database = Database.CreateContext();
            var row = new PushSubscription
            {
                UserId = 1,
                Endpoint = endpoint,
                P256dh = "p",
                Auth = "a",
                VapidKeyId = keyId ?? KeyId,
                State = state,
                CreatedAtMs = 1,
                UpdatedAtMs = 1
            };
            database.PushSubscriptions.Add(row);
            database.SaveChanges();
            return row.Id;
        }

        internal void SeedSettings(bool enabled = true, string time = "22:00", string? mutedUntil = null,
            int window = 90)
        {
            using var database = Database.CreateContext();
            database.ReminderSettings.Add(new ReminderSettings
            {
                UserId = 1,
                Enabled = enabled,
                ReminderTime = time,
                MutedUntil = mutedUntil,
                SendWindowMin = window,
                UpdatedAtMs = 1
            });
            database.SaveChanges();
        }

        internal void SeedDayStatus(string localDate, string status)
        {
            using var database = Database.CreateContext();
            database.DayStatuses.Add(new DayStatus
            {
                UserId = 1,
                LocalDate = localDate,
                Status = status,
                SessionId = null,
                DecidedAtMs = 1
            });
            database.SaveChanges();
        }

        // A claim whose process died before the first attempt (or after one), with the device row it
        // had already fanned out.
        internal void SeedClaim(string localDate, int subscriptionId, string deliveryResult = "pending",
            int attempts = 0)
        {
            using var database = Database.CreateContext();
            var log = new ReminderLog
            {
                UserId = 1,
                LocalDate = localDate,
                Kind = "daily",
                Channel = "webpush",
                Result = "sending",
                TtlS = 5400,
                ExpiresAtMs = 1,
                SkipAvailable = true,
                CreatedAtMs = 1,
                UpdatedAtMs = 1
            };
            database.ReminderLogs.Add(log);
            database.SaveChanges();
            database.PushDeliveries.Add(new PushDelivery
            {
                ReminderLogId = log.Id,
                SubscriptionId = subscriptionId,
                Attempts = attempts,
                Result = deliveryResult,
                UpdatedAtMs = 1
            });
            database.SaveChanges();
        }

        internal async Task<List<ReminderLog>> DecisionsAsync()
        {
            await using var database = Database.CreateContext();
            return await database.ReminderLogs.AsNoTracking().OrderBy(row => row.Id)
                .ToListAsync(CancellationToken.None);
        }

        internal async Task<List<PushDelivery>> DeliveriesAsync()
        {
            await using var database = Database.CreateContext();
            return await database.PushDeliveries.AsNoTracking().OrderBy(row => row.Id)
                .ToListAsync(CancellationToken.None);
        }

        internal async Task<PushSubscription> SubscriptionAsync(int id)
        {
            await using var database = Database.CreateContext();
            return await database.PushSubscriptions.AsNoTracking().SingleAsync(row => row.Id == id,
                CancellationToken.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    // Answers `first` once and `later` for every read after it, so a caller that reads the clock more
    // than once in one pass disagrees with itself about which local day it is.
    private sealed class FlippingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _first;
        private readonly DateTimeOffset _later;
        private int _reads;

        internal FlippingTimeProvider(DateTimeOffset first, DateTimeOffset later)
        {
            _first = first;
            _later = later;
        }

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1 ? _first : _later;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly Action<(LogLevel Level, string Message)> _sink;

        internal ListLogger(Action<(LogLevel Level, string Message)> sink)
        {
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _sink((logLevel, formatter(state, exception)));
    }
}
