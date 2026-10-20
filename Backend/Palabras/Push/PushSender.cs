using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Palabras.Persistence;

namespace Palabras.Push;

// One message to deliver. The payload is the final JSON the service worker reads (plan §3.7); the
// delivery loop (U6) chooses the TTL, urgency and topic, this unit only transports them.
internal sealed record PushOutgoingMessage(string Payload, int TimeToLiveSeconds, PushUrgency Urgency, string? Topic);

// What one attempt produced, including the values written to the database.
internal sealed record PushSendResult(int SubscriptionId, PushOutcome Outcome, int? StatusCode, string? Failure);

// Delivers one message to one stored subscription and writes the per-attempt accounting (plan §3.8):
// the subscription's own state and failure counters, plus the push_deliveries row when the attempt
// belongs to a reminder decision. Every path records a non-null `last_status`, which is what lets
// the registration routes tell a learner unsubscribe (NULL) from a push-service refusal.
internal sealed class PushSender
{
    // Plan §3.8: twenty consecutive transport failures on one device is worth an operator warning,
    // and transient is still not gone — the device is kept.
    internal const int FailureWarningThreshold = 20;

    private readonly DatabaseSettings _settings;
    private readonly IPushTransport _transport;
    private readonly TimeProvider _time;
    private readonly ILogger<PushSender> _logger;

    public PushSender(DatabaseSettings settings, IPushTransport transport, TimeProvider time,
        ILogger<PushSender> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _transport = transport;
        _time = time;
        _logger = logger;
    }

    // Loads the row by id so the caller cannot pass a tracked entity from another context. A row
    // that is not active, or that is bound to another VAPID key, is skipped without a network call:
    // the push service would refuse it and the plan wants it marked instead (plan §2.2).
    internal async Task<PushSendResult> SendAsync(int subscriptionId, PushOutgoingMessage message, VapidKeys keys,
        int? reminderLogId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(keys);
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await using var database = _settings.CreateContext();
        var subscription = await database.PushSubscriptions
            .SingleOrDefaultAsync(row => row.Id == subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return new PushSendResult(subscriptionId, PushOutcome.NotActive, null, null);
        }
        if (!string.Equals(subscription.State, PushSubscriptionLifecycle.Active, StringComparison.Ordinal))
        {
            return new PushSendResult(subscriptionId, PushOutcome.NotActive, null, null);
        }
        if (!string.Equals(subscription.VapidKeyId, keys.KeyId, StringComparison.Ordinal))
        {
            // The key was rotated: this row can only be healed by the browser subscribing again.
            subscription.State = PushSubscriptionLifecycle.StaleKey;
            subscription.UpdatedAtMs = nowMs;
            await database.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Push subscription {SubscriptionId} is bound to another VAPID key (current {KeyId}); skipped.",
                subscription.Id, keys.KeyId);
            return new PushSendResult(subscription.Id, PushOutcome.StaleKey, null, null);
        }

        var transportResult = await _transport.SendAsync(
            new PushTransportRequest(subscription.Endpoint, subscription.P256dh, subscription.Auth, message.Payload,
                message.TimeToLiveSeconds, message.Urgency, message.Topic),
            keys, cancellationToken);
        var outcome = PushOutcomeClassifier.Classify(transportResult);
        var status = PushOutcomeClassifier.StatusFor(transportResult);
        var delivery = await TrackDeliveryAsync(database, reminderLogId, subscription.Id, cancellationToken);
        switch (outcome)
        {
            case PushOutcome.Delivered:
                subscription.LastSuccessAtMs = nowMs;
                subscription.LastStatus = status;
                subscription.FailureCount = 0;
                subscription.UpdatedAtMs = nowMs;
                break;
            case PushOutcome.Gone:
                // 404/410: the browser dropped the subscription or it expired. The endpoint is a
                // secret and useless now, so the row goes (plan §3.8); the attempt row keeps the
                // device id only in the log, never in the database.
                database.PushSubscriptions.Remove(subscription);
                if (delivery is not null)
                {
                    delivery.SubscriptionId = null;
                }
                _logger.LogInformation(
                    "Push subscription {SubscriptionId} is gone ({Status}) at {Host}; removed and never retried.",
                    subscription.Id, status, WebPushTransport.HostOf(subscription.Endpoint));
                break;
            case PushOutcome.Rejected:
                // Always with its status: `rejected` + NULL last_status is reserved for the
                // learner's own unsubscribe and must stay un-healable (PushSubscriptionLifecycle).
                PushSubscriptionLifecycle.Reject(subscription, status, nowMs);
                _logger.LogWarning(
                    "Push service rejected subscription {SubscriptionId} ({Status}) at {Host}; automatic sends stopped.",
                    subscription.Id, status, WebPushTransport.HostOf(subscription.Endpoint));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unexpected delivery outcome.");
            case PushOutcome.Transient:
                subscription.FailureCount++;
                subscription.LastFailureAtMs = nowMs;
                subscription.LastStatus = status;
                subscription.UpdatedAtMs = nowMs;
                if (subscription.FailureCount == FailureWarningThreshold)
                {
                    // Once, when the threshold is crossed. A daily repeat would need a column the
                    // frozen schema does not have; crossing 20 is the actionable signal.
                    _logger.LogWarning(
                        "Push subscription {SubscriptionId} has failed {Count} times consecutively ({Failure}); " +
                        "the device is kept and retried.",
                        subscription.Id, subscription.FailureCount, transportResult.Failure ?? "http");
                }
                break;
        }
        if (delivery is not null)
        {
            delivery.Attempts++;
            delivery.Result = PushOutcomeClassifier.Record(outcome);
            delivery.LastStatus = status;
            delivery.UpdatedAtMs = nowMs;
        }
        await database.SaveChangesAsync(cancellationToken);
        return new PushSendResult(subscription.Id, outcome, status, transportResult.Failure);
    }

    // A test send has no reminder decision behind it, and push_deliveries requires one (the plan
    // keeps test sends out of reminder_log), so the caller's null reminderLogId simply means "no
    // per-attempt row". The reminder loop (U6) always supplies one.
    private static async Task<PushDelivery?> TrackDeliveryAsync(PalabrasDbContext database, int? reminderLogId,
        int subscriptionId, CancellationToken cancellationToken)
    {
        if (reminderLogId is not { } logId)
        {
            return null;
        }
        var delivery = await database.PushDeliveries.SingleOrDefaultAsync(
            row => row.ReminderLogId == logId && row.SubscriptionId == subscriptionId, cancellationToken);
        if (delivery is not null)
        {
            return delivery;
        }
        delivery = new PushDelivery { ReminderLogId = logId, SubscriptionId = subscriptionId };
        database.PushDeliveries.Add(delivery);
        return delivery;
    }
}
