namespace Palabras.Push;

// How urgent a push service should treat a message (RFC 8030 §5.3). Kept as an enum rather than a
// string so no caller can send an urgency the push service would reject.
internal enum PushUrgency
{
    Normal,
    High
}

// One VAPID-signed send to one stored subscription. The payload is already the final JSON the
// service worker will receive; the transport only encrypts it for this device.
internal sealed record PushTransportRequest(
    string Endpoint, string P256dh, string Auth, string Payload, int TimeToLiveSeconds,
    PushUrgency Urgency, string? Topic);

// What the push service answered. StatusCode is null when no HTTP response was received at all
// (timeout, DNS, TLS, connection reset); Failure then names the transport-level cause. RetryAfter
// is the push service's own hint (429), which only the retry loop (U6) may act on.
internal sealed record PushTransportResult(int? StatusCode, TimeSpan? RetryAfter, string? Failure);

// The single seam between delivery accounting and the Web Push library (plan §4 U4): tests
// substitute this to assert the request that would be sent, and R9 keeps a hand-rolled RFC 8291
// implementation possible behind the same interface.
internal interface IPushTransport
{
    Task<PushTransportResult> SendAsync(PushTransportRequest request, VapidKeys keys,
        CancellationToken cancellationToken);
}
