using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Time.Testing;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 U4 delivery accounting (plan §2.4, §3.8). One attempt is one push_deliveries row: the carry-over
// between "sent" and "recorded" is the only way a retry can be resumable, so the row is asserted
// field by field (attempts, result, response code) instead of only through the route's response.
public sealed class M8PushSenderTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-20T22:00:00Z", CultureInfo.InvariantCulture);

    private readonly string _root = Path.Combine("/tmp", "palabras-push", "sender-" + Guid.NewGuid().ToString("N"));
    private readonly VapidKeys _keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));

    public M8PushSenderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task OneAttemptProducesExactlyOneDeliveryRow()
    {
        var (settings, subscriptionId, logId) = Seed();
        var transport = new ScriptedPushTransport();

        var result = await SendAsync(settings, transport, subscriptionId, logId);

        Assert.Equal(PushOutcome.Delivered, result.Outcome);
        Assert.Equal(201, result.StatusCode);
        var subscription = await SubscriptionAsync(settings, subscriptionId);
        Assert.Equal(PushSubscriptionLifecycle.Active, subscription.State);
        Assert.Equal(201, subscription.LastStatus);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), subscription.LastSuccessAtMs);
        Assert.Equal(0, subscription.FailureCount);
        var delivery = Assert.Single(await DeliveriesAsync(settings));
        Assert.Equal(logId, delivery.ReminderLogId);
        Assert.Equal(subscriptionId, delivery.SubscriptionId);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal("sent", delivery.Result);
        Assert.Equal(201, delivery.LastStatus);
    }

    [Fact]
    public async Task ARetryUpdatesTheSameRowAndBumpsTheAttemptCount()
    {
        var (settings, subscriptionId, logId) = Seed();
        var transport = new ScriptedPushTransport();
        transport.Answer(503);

        var failed = await SendAsync(settings, transport, subscriptionId, logId);
        Assert.Equal(PushOutcome.Transient, failed.Outcome);
        await using (var afterFailure = settings.CreateContext())
        {
            var delivery = Assert.Single(await afterFailure.PushDeliveries.AsNoTracking()
                .ToListAsync(CancellationToken.None));
            Assert.Equal(1, delivery.Attempts);
            Assert.Equal("transient", delivery.Result);
            Assert.Equal(503, delivery.LastStatus);
            var subscription = await afterFailure.PushSubscriptions.AsNoTracking()
                .SingleAsync(CancellationToken.None);
            Assert.Equal(1, subscription.FailureCount);
            Assert.Equal(PushSubscriptionLifecycle.Active, subscription.State);
        }

        var delivered = await SendAsync(settings, transport, subscriptionId, logId);
        Assert.Equal(PushOutcome.Delivered, delivered.Outcome);
        await using var context = settings.CreateContext();
        // The UNIQUE (reminder_log_id, subscription_id) is the resume key: the second attempt is the
        // same row, not a second one.
        var updated = Assert.Single(await context.PushDeliveries.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Equal(2, updated.Attempts);
        Assert.Equal("sent", updated.Result);
        Assert.Equal(201, updated.LastStatus);
        var healed = await context.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(0, healed.FailureCount);
    }

    [Fact]
    public async Task AGoneDeliveryKeepsItsAttemptWithoutTheSubscription()
    {
        var (settings, subscriptionId, logId) = Seed();
        var transport = new ScriptedPushTransport();
        transport.Answer(410);

        var result = await SendAsync(settings, transport, subscriptionId, logId);

        Assert.Equal(PushOutcome.Gone, result.Outcome);
        await using var context = settings.CreateContext();
        Assert.Empty(await context.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
        var delivery = Assert.Single(await context.PushDeliveries.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Null(delivery.SubscriptionId);
        Assert.Equal(logId, delivery.ReminderLogId);
        Assert.Equal("gone", delivery.Result);
        Assert.Equal(410, delivery.LastStatus);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task ARejectionRecordsItsStatusAndStaysHealable()
    {
        var (settings, subscriptionId, logId) = Seed();
        var transport = new ScriptedPushTransport();
        transport.Answer(403);

        var result = await SendAsync(settings, transport, subscriptionId, logId);

        Assert.Equal(PushOutcome.Rejected, result.Outcome);
        var subscription = await SubscriptionAsync(settings, subscriptionId);
        Assert.Equal(PushSubscriptionLifecycle.Rejected, subscription.State);
        // Non-null: "rejected with no status" means the learner unsubscribed, and must not be
        // produced by a push service refusal.
        Assert.Equal(403, subscription.LastStatus);
        Assert.False(PushSubscriptionLifecycle.IsRevoked(subscription));
        var delivery = Assert.Single(await DeliveriesAsync(settings));
        Assert.Equal("rejected", delivery.Result);
        Assert.Equal(403, delivery.LastStatus);
    }

    [Fact]
    public async Task ASubscriptionBoundToAnotherKeyIsMarkedAndNeverSentTo()
    {
        var (settings, subscriptionId, logId) = Seed(vapidKeyId: "0123456789abcdef");
        var transport = new ScriptedPushTransport();

        var result = await SendAsync(settings, transport, subscriptionId, logId);

        Assert.Equal(PushOutcome.StaleKey, result.Outcome);
        Assert.Empty(transport.Requests);
        var subscription = await SubscriptionAsync(settings, subscriptionId);
        Assert.Equal(PushSubscriptionLifecycle.StaleKey, subscription.State);
        // No attempt happened, so there is no attempt row to account for.
        Assert.Empty(await DeliveriesAsync(settings));
    }

    [Fact]
    public async Task ARevokedSubscriptionIsNeverSentTo()
    {
        var (settings, subscriptionId, logId) = Seed(state: PushSubscriptionLifecycle.Rejected, lastStatus: null);
        var transport = new ScriptedPushTransport();

        var result = await SendAsync(settings, transport, subscriptionId, logId);

        Assert.Equal(PushOutcome.NotActive, result.Outcome);
        Assert.Empty(transport.Requests);
        Assert.Empty(await DeliveriesAsync(settings));
    }

    [Fact]
    public async Task CrossingTheFailureThresholdWarnsOnceAndKeepsTheDevice()
    {
        var (settings, subscriptionId, logId) = Seed(failureCount: PushSender.FailureWarningThreshold - 1);
        var transport = new ScriptedPushTransport();
        transport.Answer(502);
        var logs = new List<(LogLevel Level, string Message)>();

        var result = await SendAsync(settings, transport, subscriptionId, logId, logs.Add);

        Assert.Equal(PushOutcome.Transient, result.Outcome);
        var subscription = await SubscriptionAsync(settings, subscriptionId);
        Assert.Equal(PushSubscriptionLifecycle.Active, subscription.State);
        Assert.Equal(PushSender.FailureWarningThreshold, subscription.FailureCount);
        Assert.Contains(logs, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("failed 20 times consecutively", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PushSender Sender(DatabaseSettings database, IPushTransport transport,
        Action<(LogLevel Level, string Message)>? log = null) =>
        new(database, transport, new FakeTimeProvider(Now), new ListLogger(log));

    private Task<PushSendResult> SendAsync(DatabaseSettings database, IPushTransport transport, int subscriptionId,
        int logId, Action<(LogLevel Level, string Message)>? log = null) =>
        Sender(database, transport, log).SendAsync(subscriptionId,
            new PushOutgoingMessage("{\"v\":1,\"kind\":\"daily\"}", 900, PushUrgency.Normal, "daily-2026-10-20"),
            _keys, logId, CancellationToken.None);

    private (DatabaseSettings Database, int SubscriptionId, int LogId) Seed(string? vapidKeyId = null,
        string state = PushSubscriptionLifecycle.Active, int? lastStatus = null, int failureCount = 0)
    {
        var settings = new DatabaseSettings(Path.Combine(_root, "palabras.db"));
        using var database = settings.CreateContext();
        database.Database.Migrate();
        var stamp = DateTimeOffset.UnixEpoch;
        database.Users.Add(new User
        {
            Id = 1, Username = "learner", Role = "owner", PasswordHash = "hash",
            CreatedAt = stamp, UpdatedAt = stamp
        });
        var subscription = new PushSubscription
        {
            UserId = 1,
            Endpoint = "https://fcm.googleapis.com/fcm/send/device",
            P256dh = "p", Auth = "a",
            VapidKeyId = vapidKeyId ?? _keys.KeyId,
            State = state,
            LastStatus = lastStatus,
            FailureCount = failureCount,
            CreatedAtMs = 1,
            UpdatedAtMs = 1
        };
        var reminder = new ReminderLog
        {
            UserId = 1, LocalDate = "2026-10-20", Kind = "daily", Channel = "webpush", Result = "sending",
            SkipAvailable = true, CreatedAtMs = 1, UpdatedAtMs = 1
        };
        database.PushSubscriptions.Add(subscription);
        database.ReminderLogs.Add(reminder);
        database.SaveChanges();
        return (settings, subscription.Id, reminder.Id);
    }

    private static async Task<PushSubscription> SubscriptionAsync(DatabaseSettings settings, int id)
    {
        await using var database = settings.CreateContext();
        return await database.PushSubscriptions.AsNoTracking().SingleAsync(row => row.Id == id,
            CancellationToken.None);
    }

    private static async Task<List<PushDelivery>> DeliveriesAsync(DatabaseSettings settings)
    {
        await using var database = settings.CreateContext();
        return await database.PushDeliveries.AsNoTracking().ToListAsync(CancellationToken.None);
    }

    private sealed class ListLogger : ILogger<PushSender>
    {
        private readonly Action<(LogLevel Level, string Message)>? _sink;

        internal ListLogger(Action<(LogLevel Level, string Message)>? sink) => _sink = sink;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _sink?.Invoke((logLevel, formatter(state, exception)));
    }
}
