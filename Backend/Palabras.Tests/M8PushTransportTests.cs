using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 U4 transport contract (plan §3.7, §3.8, §4 U4). A fake push service stands in for FCM: the
// assertions are about the request that *would* be sent (VAPID JWT, encryption headers, TTL,
// urgency, topic, redirects never followed) and about how each answer is classified. Nothing here
// touches the network.
public sealed class M8PushTransportTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-20T21:00:00Z", CultureInfo.InvariantCulture);
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/device-token";
    private static readonly PushSettings Settings = new(true, "/tmp/palabras-push/unused-vapid.json",
        "https://palabras.siebentower.nl");

    [Fact]
    public async Task ATestSendIsVapidSignedAndEncryptedForThatDevice()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        using var transport = Create(service);
        const string payload = "{\"v\":1,\"kind\":\"test\"}";

        var result = await transport.SendAsync(
            new PushTransportRequest(Endpoint, subscriber.P256dh, subscriber.Auth, payload, 60, PushUrgency.High,
                null), keys, CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        Assert.Null(result.Failure);
        var request = Assert.Single(service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint, request.Uri.ToString());

        // RFC 8292: `vapid t=<jwt>, k=<uncompressed P-256 public key>`. The key is what a push
        // service uses to verify the JWT, so it must be the deployment's current public key.
        var authorization = Assert.IsType<AuthenticationHeaderValue>(request.Authorization);
        Assert.Equal("vapid", authorization.Scheme);
        var parameters = ParseVapidParameters(authorization.Parameter!);
        Assert.Equal(keys.PublicKey, parameters["k"]);
        var claims = AssertVapidJwt(parameters["t"], keys);
        Assert.Equal("https://fcm.googleapis.com", claims.Audience);
        Assert.Equal("https://palabras.siebentower.nl", claims.Subject);
        // RFC 8292 caps the token lifetime at 24 hours. The library stamps the token from the wall
        // clock, not from the app's TimeProvider, so the bounds are relative to now.
        Assert.InRange(claims.Expires, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(24).AddMinutes(1));

        Assert.Equal("aes128gcm", request.ContentEncoding);
        Assert.Equal("application/octet-stream", request.ContentType);
        Assert.Equal("60", request.TimeToLive);
        Assert.Equal("high", request.Urgency);
        Assert.False(request.Headers.ContainsKey("Topic"));

        // The body is the RFC 8291 header plus ciphertext plus the 16-byte tag; decrypting it with
        // the device's private key must give back exactly the JSON the service worker will read.
        Assert.Equal(payload, Encoding.UTF8.GetString(
            Rfc8291.Decrypt(request.Body, subscriber.PrivateKey, subscriber.PublicKey, subscriber.AuthBytes)));
        Assert.Equal(21 + 65 + Encoding.UTF8.GetByteCount(payload) + 1 + 16, request.Body.Length);
    }

    [Fact]
    public async Task EveryAttemptUsesAFreshEphemeralKeyAndSalt()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        using var transport = Create(service);
        var request = new PushTransportRequest(Endpoint, subscriber.P256dh, subscriber.Auth, "{\"v\":1,\"kind\":\"test\"}",
            60, PushUrgency.High, null);

        await transport.SendAsync(request, keys, CancellationToken.None);
        await transport.SendAsync(request, keys, CancellationToken.None);

        Assert.Equal(2, service.Requests.Count);
        var first = service.Requests[0].Body;
        var second = service.Requests[1].Body;
        // Different salt and different ephemeral key: two deliveries must not be linkable, and a
        // reused (key, salt) pair would break the encryption's security assumptions.
        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first[21..86], second[21..86]);
        foreach (var body in new[] { first, second })
        {
            Assert.Equal("{\"v\":1,\"kind\":\"test\"}", Encoding.UTF8.GetString(
                Rfc8291.Decrypt(body, subscriber.PrivateKey, subscriber.PublicKey, subscriber.AuthBytes)));
        }
    }

    [Fact]
    public async Task ADailyMessageCarriesItsTopicAndTtl()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        using var transport = Create(service);

        var result = await transport.SendAsync(
            new PushTransportRequest(Endpoint, subscriber.P256dh, subscriber.Auth, "{}", 3600, PushUrgency.Normal,
                "daily-2026-10-20"), keys, CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal("daily-2026-10-20", request.Topic);
        Assert.Equal("3600", request.TimeToLive);
        Assert.Equal("normal", request.Urgency);
    }

    [Theory]
    [InlineData(200, "Delivered")]
    [InlineData(201, "Delivered")]
    [InlineData(202, "Delivered")]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Rejected")]
    [InlineData(403, "Rejected")]
    [InlineData(404, "Gone")]
    [InlineData(410, "Gone")]
    [InlineData(413, "Rejected")]
    [InlineData(429, "Transient")]
    [InlineData(500, "Transient")]
    [InlineData(503, "Transient")]
    public async Task EveryPushServiceAnswerIsClassified(int status, string expected)
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Answer((HttpStatusCode)status);
        using var transport = Create(service);

        var result = await transport.SendAsync(Send(subscriber), keys, CancellationToken.None);

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(expected, PushOutcomeClassifier.Classify(result).ToString());
        Assert.Equal(status, PushOutcomeClassifier.StatusFor(result));
    }

    [Fact]
    public async Task ARedirectIsNeverFollowedAndCountsAsRejected()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Answer(HttpStatusCode.Redirect, response =>
            response.Headers.Location = new Uri("https://127.0.0.1/fcm/send/elsewhere"));
        using var transport = Create(service);

        var result = await transport.SendAsync(Send(subscriber), keys, CancellationToken.None);

        // One request only: a push endpoint must not be able to bounce the POST somewhere else.
        Assert.Single(service.Requests);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal(PushOutcome.Rejected, PushOutcomeClassifier.Classify(result));
        // The production handler is what makes that true; the fake handler below cannot redirect.
        using var handler = WebPushTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task ATimeoutIsTransientWithNoStatusAndNoRetryAfter()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Throw(() => new TaskCanceledException("The request timed out.", new TimeoutException()));
        using var transport = Create(service);

        var result = await transport.SendAsync(Send(subscriber), keys, CancellationToken.None);

        Assert.Null(result.StatusCode);
        Assert.Equal("timeout", result.Failure);
        Assert.Equal(PushOutcome.Transient, PushOutcomeClassifier.Classify(result));
        // No HTTP status was received, so the frozen integer column gets a value HTTP never sends.
        Assert.Equal(PushOutcomeClassifier.NoResponseStatus, PushOutcomeClassifier.StatusFor(result));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "dns")]
    [InlineData(HttpRequestError.SecureConnectionError, "tls")]
    [InlineData(HttpRequestError.ConnectionError, "network")]
    public async Task NetworkFailuresAreTransientAndNamed(HttpRequestError error, string failure)
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Throw(() => new HttpRequestException(error, "no route"));
        using var transport = Create(service);

        var result = await transport.SendAsync(Send(subscriber), keys, CancellationToken.None);

        Assert.Null(result.StatusCode);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(PushOutcome.Transient, PushOutcomeClassifier.Classify(result));
    }

    [Fact]
    public async Task AThrottledPushServiceReportsItsRetryAfterHint()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Answer((HttpStatusCode)429, response => response.Headers.RetryAfter =
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        using var transport = Create(service);

        var result = await transport.SendAsync(Send(subscriber), keys, CancellationToken.None);

        Assert.Equal(429, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal(PushOutcome.Transient, PushOutcomeClassifier.Classify(result));
    }

    [Fact]
    public async Task TheCallersCancellationIsNotSwallowedAsATimeout()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var subscriber = SubscriberKeys.Create();
        var service = new FakePushService();
        service.Throw(() => new OperationCanceledException("cancelled"));
        using var transport = Create(service);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(Send(subscriber), keys, cancellation.Token));
    }

    [Fact]
    public void TheProductionClientIsBoundedAndNeverRetriesByItself()
    {
        var keys = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        using var transport = Create(new FakePushService());
        using var handler = WebPushTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(WebPushTransport.TimeoutSeconds), transport.Timeout);
    }

    // ---- helpers ----------------------------------------------------------

    private static WebPushTransport Create(HttpMessageHandler handler) =>
        new(Settings, NullLogger<WebPushTransport>.Instance, handler);

    private static PushTransportRequest Send(SubscriberKeys subscriber) => new(Endpoint, subscriber.P256dh,
        subscriber.Auth, "{\"v\":1,\"kind\":\"test\"}", 60, PushUrgency.High, null);

    private static Dictionary<string, string> ParseVapidParameters(string value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            Assert.True(separator > 0, "VAPID header parameter without '=': " + part);
            parameters[part[..separator]] = part[(separator + 1)..];
        }
        return parameters;
    }

    // Decodes and verifies the ES256 JWT a push service would check before touching the endpoint.
    private static VapidClaims AssertVapidJwt(string token, VapidKeys keys)
    {
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);
        using var header = JsonDocument.Parse(Base64Url.Decode(segments[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        using var payload = JsonDocument.Parse(Base64Url.Decode(segments[1]));
        var signature = Base64Url.Decode(segments[2]);
        Assert.Equal(64, signature.Length);
        var publicKey = Base64Url.Decode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] }
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("exp").GetInt64());
        return new VapidClaims(
            payload.RootElement.TryGetProperty("aud", out var aud) ? aud.GetString()! : "",
            payload.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString()! : "",
            expires);
    }

    private sealed record VapidClaims(string Audience, string Subject, DateTimeOffset Expires);

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization, string? ContentEncoding,
        string? ContentType, string? TimeToLive, string? Urgency, string? Topic,
        IReadOnlyDictionary<string, string[]> Headers, byte[] Body);

    // A push service that records exactly what the transport sent and answers from a script.
    private sealed class FakePushService : HttpMessageHandler
    {
        private readonly Queue<(Func<HttpResponseMessage>? Answer, Func<Exception>? Failure)> _script = new();
        private HttpStatusCode _defaultStatus = HttpStatusCode.Created;

        internal List<CapturedRequest> Requests { get; } = [];

        internal void Answer(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) =>
            _script.Enqueue((() =>
            {
                var response = new HttpResponseMessage(status);
                configure?.Invoke(response);
                return response;
            }, null));

        internal void Throw(Func<Exception> failure) => _script.Enqueue((null, failure));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization,
                request.Content is null ? null : HeaderOrNull(request.Content.Headers, "Content-Encoding"),
                request.Content?.Headers.ContentType?.MediaType,
                HeaderOrNull(request.Headers, "TTL"),
                HeaderOrNull(request.Headers, "Urgency"),
                HeaderOrNull(request.Headers, "Topic"),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                body));
            if (_script.Count == 0)
            {
                return new HttpResponseMessage(_defaultStatus);
            }
            var (answer, failure) = _script.Dequeue();
            return failure is not null ? throw failure() : answer!();
        }

        private static string? HeaderOrNull(HttpHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
    }

    private sealed record SubscriberKeys(byte[] PrivateKey, byte[] PublicKey, string P256dh, string Auth, byte[] AuthBytes)
    {
        internal static SubscriberKeys Create()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
            var publicKey = new byte[65];
            publicKey[0] = 0x04;
            parameters.Q.X!.CopyTo(publicKey, 1);
            parameters.Q.Y!.CopyTo(publicKey, 33);
            var auth = RandomNumberGenerator.GetBytes(16);
            return new SubscriberKeys(parameters.D!, publicKey, Base64Url.Encode(publicKey), Base64Url.Encode(auth),
                auth);
        }
    }

    // An independent RFC 8291 decryption written from the spec, not from the library: if both sides
    // shared a mistake the round-trip would still pass, so the test decrypts the way a browser does
    // (ECDH over the ephemeral key in the header, auth secret as salt, "WebPush: info" as key info,
    // then the content-coding keys) and checks the exact bytes.
    private static class Rfc8291
    {
        internal static byte[] Decrypt(byte[] body, byte[] subscriberPrivateKey, byte[] subscriberPublicKey,
            byte[] authSecret)
        {
            var salt = body[..16];
            var keyIdLength = body[20];
            var serverKey = body[21..(21 + keyIdLength)];
            var ciphertext = body[(21 + keyIdLength)..];
            using var server = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = serverKey[1..33], Y = serverKey[33..] }
            });
            using var subscriber = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = subscriberPrivateKey,
                Q = new ECPoint { X = subscriberPublicKey[1..33], Y = subscriberPublicKey[33..] }
            });
            var sharedSecret = subscriber.DeriveRawSecretAgreement(server.PublicKey);
            var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), subscriberPublicKey, serverKey);
            var inputKeyMaterial = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
            var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, 16, salt,
                Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, 12, salt,
                Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
            var plaintext = new byte[ciphertext.Length - 16];
            using var aes = new AesGcm(contentKey, 16);
            aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plaintext);
            // Single record: the payload, then 0x02 and optional 0x00 padding.
            var end = plaintext.Length - 1;
            while (end >= 0 && plaintext[end] == 0)
            {
                end--;
            }
            Assert.True(end >= 0 && plaintext[end] == 0x02, "RFC 8291 record delimiter missing.");
            return plaintext[..end];
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var result = new byte[parts.Sum(part => part.Length)];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(result, offset);
                offset += part.Length;
            }
            return result;
        }
    }

    private static class Base64Url
    {
        internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static byte[] Decode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
        }
    }
}
