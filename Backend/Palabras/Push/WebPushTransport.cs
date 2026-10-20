using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using WireSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Palabras.Push;

// The only place the Web Push library is used (plan §4 U4, D2/R9). It signs the request with the
// deployment's VAPID key, encrypts the payload for the device (RFC 8291) and reports what the push
// service answered. Classification and every database write live in PushSender, so this class stays
// a thin protocol adapter that tests can point at a fake push service.
internal sealed class WebPushTransport : IPushTransport, IDisposable
{
    // The plan's §3.8 timeout. A push service that has not answered in ten seconds is not going to,
    // and the caller's per-attempt accounting needs a bounded wait.
    internal const int TimeoutSeconds = 10;

    private readonly PushSettings _settings;
    private readonly ILogger<WebPushTransport> _logger;
    private readonly HttpClient _client;
    private readonly PushServiceClient _clientForWebPush;
    private readonly StatusRecorder _recorder;
    // The status recorder has to be read back after the awaited call, so sends are serialised. This
    // is also what the delivery loop does (plan §2.4 step 3), and one container sends one reminder
    // a minute, so the cost is nil next to the accountability it buys.
    private readonly SemaphoreSlim _oneSendAtATime = new(1, 1);

    // Production wiring: redirects are never followed. A push endpoint is operator/device-supplied,
    // and following a 3xx would let it bounce the POST to a host the SSRF guard never saw.
    internal WebPushTransport(PushSettings settings, ILogger<WebPushTransport> logger)
        : this(settings, logger, CreateHandler())
    {
    }

    internal WebPushTransport(PushSettings settings, ILogger<WebPushTransport> logger, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(handler);
        _settings = settings;
        _logger = logger;
        _recorder = new StatusRecorder(handler);
        _client = new HttpClient(new UrgencyHeaderHandler(_recorder)) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        // The library retries 429 by itself by default. Every attempt must be accounted for, and the
        // retry budget belongs to the delivery loop (plan §3.8, D12), so automatic retries are off.
        _clientForWebPush = new PushServiceClient(_client)
        {
            AutoRetryAfter = false,
            MaxRetriesAfter = 0
        };
    }

    // Exposed so a test can prove the production client is bounded (plan §3.8: 10 s).
    internal TimeSpan Timeout => _client.Timeout;

    internal static SocketsHttpHandler CreateHandler() => new()    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public async Task<PushTransportResult> SendAsync(PushTransportRequest request, VapidKeys keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keys);
        await _oneSendAtATime.WaitAsync(cancellationToken);
        try
        {
            _recorder.Reset();
            var subscription = new WireSubscription { Endpoint = request.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, request.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, request.Auth);
            var message = new PushMessage(request.Payload)
            {
                TimeToLive = request.TimeToLiveSeconds,
                Urgency = request.Urgency == PushUrgency.High ? PushMessageUrgency.High : PushMessageUrgency.Normal,
                Topic = request.Topic
            };
            using var authentication = new VapidAuthentication(keys.PublicKey, keys.PrivateKey)
            {
                Subject = _settings.Subject
            };
            await _clientForWebPush.RequestPushMessageDeliveryAsync(subscription, message, authentication,
                VapidAuthenticationScheme.Vapid, cancellationToken);
            // The library throws on every non-2xx answer, so reaching here means the push service
            // accepted the message. The recorder supplies the exact code (201 or 202).
            return new PushTransportResult(_recorder.LastStatus, null, null);
        }
        catch (PushServiceClientException exception)
        {
            var status = (int)exception.StatusCode;
            if (status is < 100 or > 599)
            {
                // The library failed before a response could be read; nothing HTTP-shaped to record.
                return new PushTransportResult(null, RetryAfter(exception.Headers), "request_failed");
            }
            return new PushTransportResult(status, RetryAfter(exception.Headers), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation with a TimeoutException inside.
            return new PushTransportResult(null, null, "timeout");
        }
        catch (HttpRequestException exception)
        {
            var failure = exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "dns",
                HttpRequestError.SecureConnectionError => "tls",
                _ => "network"
            };
            _logger.LogWarning("Push delivery to {Host} failed before a response ({Failure}).",
                HostOf(request.Endpoint), failure);
            return new PushTransportResult(null, null, failure);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException
            or CryptographicException or InvalidOperationException)
        {
            // A stored subscription whose keys cannot be turned into an encrypted payload: a local
            // failure, not a push-service answer. Recorded as transient so it surfaces in the
            // failure count instead of silently revoking a device that may be perfectly healthy.
            return new PushTransportResult(null, null, "encryption_failed");
        }
        finally
        {
            _oneSendAtATime.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _oneSendAtATime.Dispose();
    }

    private static TimeSpan? RetryAfter(HttpResponseHeaders? headers) =>
        headers?.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : null;

    // Endpoint hosts are safe to log; the endpoint itself is a bearer-like token and never is.
    internal static string HostOf(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid";

    // RFC 8030 §5.3 defines an absent Urgency header as "normal", and the library therefore omits
    // it for a normal message. The plan's contract (§3.7) sends it explicitly, so that what a
    // reviewer (or a proxy log) sees is what the push service receives, it is filled in here.
    private sealed class UrgencyHeaderHandler : DelegatingHandler
    {
        internal UrgencyHeaderHandler(HttpMessageHandler handler) : base(handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("Urgency"))
            {
                request.Headers.TryAddWithoutValidation("Urgency", "normal");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    // Records the response of the single in-flight send so a 2xx answer's exact status survives:
    // the Web Push library returns no response object on success.
    private sealed class StatusRecorder : DelegatingHandler
    {
        private int? _lastStatus;

        internal StatusRecorder(HttpMessageHandler handler) : base(handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
        }

        internal int? LastStatus => _lastStatus;

        internal void Reset() => _lastStatus = null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            _lastStatus = (int)response.StatusCode;
            return response;
        }
    }
}
