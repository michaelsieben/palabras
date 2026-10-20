namespace Palabras.Push;

// The four outcomes the plan's response table (§3.8) distinguishes. Expiry (Gone) is terminal for
// the subscription; Transient is retryable; Rejected stops automatic sends until the device
// re-subscribes (except 413, which is our own oversized payload: the sender records the attempt but
// leaves the subscription active); Delivered is the only success.
internal enum PushOutcome
{
    Delivered,
    Gone,
    Rejected,
    Transient,
    // Never sent: the row is bound to another VAPID key, so the push service would refuse it.
    StaleKey,
    // Never sent: the row is no longer `active` (revoked, rejected or deleted).
    NotActive
}

// Maps one push-service answer to an outcome. The rules are deliberately total: every possible
// status resolves, so no answer can leave a delivery unaccounted for.
internal static class PushOutcomeClassifier
{
    // `last_status` is NOT NULL by contract for anything this classifier records (the frozen schema
    // allows NULL so a learner unsubscribe can be told apart from a push-service refusal). A
    // transport failure has no HTTP status, so it is recorded as 0 — a value HTTP never produces.
    internal const int NoResponseStatus = 0;

    // 413 means the payload was too large — our bug, not a dead endpoint. Plan §3.8 keeps the
    // subscription `active` on this answer, so the sender records the attempt but never rejects it.
    internal const int PayloadTooLargeStatus = 413;

    internal static PushOutcome Classify(PushTransportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.StatusCode is not { } status)
        {
            // No response: timeout, DNS, TLS or a connection error. Always worth another attempt.
            return PushOutcome.Transient;
        }
        return status switch
        {
            >= 200 and < 300 => PushOutcome.Delivered,
            // The subscription is gone for good: the browser dropped it, or it expired.
            404 or 410 => PushOutcome.Gone,
            // The push service is busy or throttling us; the plan allows up to three attempts a day.
            408 or 425 or 429 => PushOutcome.Transient,
            >= 500 => PushOutcome.Transient,
            // 400/401/403/413 and any 3xx (redirects are disabled, so a 3xx is a misconfigured
            // endpoint) mean "do not send this again by itself". 413 is classified here too, but
            // the sender keeps the subscription active for it (plan §3.8: the payload, not the
            // endpoint, was at fault).
            _ => PushOutcome.Rejected
        };
    }

    // The value written to `last_status` and `push_deliveries.last_status`.
    internal static int StatusFor(PushTransportResult result) => result.StatusCode ?? NoResponseStatus;

    // `push_deliveries.result` CHECK accepts only these four (frozen schema).
    internal static string Record(PushOutcome outcome) => outcome switch
    {
        PushOutcome.Delivered => "sent",
        PushOutcome.Gone => "gone",
        PushOutcome.Rejected => "rejected",
        PushOutcome.Transient => "transient",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
            "Only an attempted delivery can be recorded.")
    };

    // Contract names used by POST /api/push/test and `push test-send`.
    internal static string Wire(PushOutcome outcome) => outcome switch
    {
        PushOutcome.Delivered => "delivered",
        PushOutcome.Gone => "gone",
        PushOutcome.Rejected => "rejected",
        PushOutcome.Transient => "transient",
        PushOutcome.StaleKey => "stale_key",
        PushOutcome.NotActive => "not_active",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
