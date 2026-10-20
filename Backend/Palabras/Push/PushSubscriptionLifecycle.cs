using Palabras.Persistence;

namespace Palabras.Push;

// The three subscription states are fixed by the U1 CHECK constraint, so revocation has no state
// of its own. A learner unsubscribe therefore writes `rejected` with `last_status` NULL: every
// push-service rejection records its HTTP status (U4), so NULL unambiguously means "the learner
// took this device out". A plain re-subscribe must not resurrect such a row; the client has to
// say `reactivate: true` (the explicit "Activar recordatorio" click), while a `stale_key` row and
// a push-service-rejected row heal from an ordinary resync PUT (plan §2.2, §3.8).
internal static class PushSubscriptionLifecycle
{
    internal const string Active = "active";
    internal const string StaleKey = "stale_key";
    internal const string Rejected = "rejected";

    internal static bool IsRevoked(PushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        // U4 must write a non-null last_status on every push-service rejection: only the learner's
        // explicit unsubscribe may leave it NULL, or the row could never heal.
        return subscription.State == Rejected && subscription.LastStatus is null;
    }

    // Soft delete: the endpoint stays as the audit key, and a push_deliveries row that referenced
    // this device keeps its subscription_id (only a 404/410 from the push service deletes rows).
    internal static void Revoke(PushSubscription subscription, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        subscription.State = Rejected;
        subscription.LastStatus = null;
        subscription.UpdatedAtMs = nowMs;
    }

    // A push-service refusal (400/401/403 or an unfollowed 3xx). Unlike Revoke this always
    // carries the status that caused it: `rejected` + NULL is the learner's own unsubscribe, and a
    // refusal that recorded no status would be indistinguishable from it — a row nothing could heal.
    internal static void Reject(PushSubscription subscription, int status, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (status <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "A rejected subscription must record the status the push service returned.");
        }
        subscription.State = Rejected;
        subscription.LastStatus = status;
        subscription.LastFailureAtMs = nowMs;
        subscription.UpdatedAtMs = nowMs;
    }
}
