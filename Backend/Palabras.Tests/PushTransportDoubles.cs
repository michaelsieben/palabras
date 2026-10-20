using Palabras.Push;

namespace Palabras.Tests;

// Shared test double for IPushTransport (M8 U4). No test may reach the network: a push delivery is
// asserted by recording the request the transport would have sent and scripting the answer the push
// service would have given.
internal sealed class ScriptedPushTransport : IPushTransport
{
    private readonly Queue<PushTransportResult> _script = new();

    internal List<PushTransportRecord> Requests { get; } = [];

    // What an unscripted send gets: a normal Web Push acceptance.
    internal PushTransportResult DefaultResult { get; set; } = new(201, null, null);

    internal ScriptedPushTransport Answer(int? status, TimeSpan? retryAfter = null, string? failure = null)
    {
        _script.Enqueue(new PushTransportResult(status, retryAfter, failure));
        return this;
    }

    public Task<PushTransportResult> SendAsync(PushTransportRequest request, VapidKeys keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keys);
        Requests.Add(new PushTransportRecord(request.Endpoint, request.P256dh, request.Auth, request.Payload,
            request.TimeToLiveSeconds, request.Urgency, request.Topic, keys.KeyId, keys.PublicKey));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_script.Count == 0 ? DefaultResult : _script.Dequeue());
    }
}

internal sealed record PushTransportRecord(
    string Endpoint, string P256dh, string Auth, string Payload, int TimeToLiveSeconds, PushUrgency Urgency,
    string? Topic, string KeyId, string PublicKey);
