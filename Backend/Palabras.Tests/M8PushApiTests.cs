using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Palabras.Accounts;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 Web Push registration contract (plan §3.1-3.3, U2/U3). Real cookie auth and the X-Palabras
// middleware against a migrated throwaway SQLite database: reviewer 403, anonymous 401, one
// active row per endpoint, soft revoke on unsubscribe, and a stale VAPID key answering 409.
public sealed class M8PushApiTests
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T09:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task ConfigRequiresALearnerAndNeverExposesThePrivateKey()
    {
        using var factory = new PushApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        await AssertErrorAsync(await anonymous.GetAsync("/api/push/config", CancellationToken.None),
            HttpStatusCode.Unauthorized, "unauthorized");

        using var reviewer = await LoginAsync(factory, "reviewer");
        await AssertErrorAsync(await reviewer.GetAsync("/api/push/config", CancellationToken.None),
            HttpStatusCode.Forbidden, "forbidden");

        using var learner = await LoginAsync(factory, "learner");
        using var response = await learner.GetAsync("/api/push/config", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var root = body.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("publicKey").GetString()));
        Assert.Matches("^[0-9a-f]{16}$", root.GetProperty("keyId").GetString());
        Assert.False(root.TryGetProperty("privateKey", out _));
        Assert.False(root.TryGetProperty("vapidKeyId", out _));
    }

    [Fact]
    public async Task PushOffMeansEveryRouteAnswers503AndNoRowAppears()
    {
        using var factory = new PushApplicationFactory(Now, pushEnabled: false);
        using var learner = await LoginAsync(factory, "learner");
        await AssertErrorAsync(await learner.GetAsync("/api/push/config", CancellationToken.None),
            HttpStatusCode.ServiceUnavailable, "push_disabled");
        var keys = NewSubscriptionKeys();
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload("https://fcm.googleapis.com/fcm/send/off", keys, "0000000000000000")),
            HttpStatusCode.ServiceUnavailable, "push_disabled");
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
            new { endpoint = "https://fcm.googleapis.com/fcm/send/off" }),
            HttpStatusCode.ServiceUnavailable, "push_disabled");
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACorruptKeyFileDisablesPushWithoutRegeneratingIt()
    {
        using var factory = new PushApplicationFactory(Now, keyFileContent: "{ not a key file");
        using var learner = await LoginAsync(factory, "learner");
        await AssertErrorAsync(await learner.GetAsync("/api/push/config", CancellationToken.None),
            HttpStatusCode.ServiceUnavailable, "push_unavailable");
        Assert.Equal("{ not a key file", await File.ReadAllTextAsync(factory.VapidPath, CancellationToken.None));
    }

    [Fact]
    public async Task SubscribeStoresTheRowAndResubscribingUpdatesInsteadOfDuplicating()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        var keys = NewSubscriptionKeys();
        var endpoint = Endpoint();

        using var first = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, keys, keyId, label: "OnePlus 15"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var body = await JsonAsync(first))
        {
            Assert.True(body.RootElement.GetProperty("id").GetInt32() > 0);
            Assert.Equal("active", body.RootElement.GetProperty("state").GetString());
        }
        int firstId;
        long created;
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            firstId = row.Id;
            created = row.CreatedAtMs;
            Assert.Equal(1, row.UserId);
            Assert.Equal(endpoint, row.Endpoint);
            Assert.Equal(keys.P256dh, row.P256dh);
            Assert.Equal(keys.Auth, row.Auth);
            Assert.Equal(keyId, row.VapidKeyId);
            Assert.Equal("OnePlus 15", row.DeviceLabel);
            Assert.Equal("active", row.State);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var rotated = NewSubscriptionKeys();
        using var second = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, rotated, keyId, label: "Tab 3"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var body = await JsonAsync(second))
        {
            Assert.Equal(firstId, body.RootElement.GetProperty("id").GetInt32());
        }
        await using var again = factory.OpenDatabase();
        var updated = await again.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(firstId, updated.Id);
        Assert.Equal(rotated.P256dh, updated.P256dh);
        Assert.True(updated.UpdatedAtMs > created);
        Assert.Equal("Tab 3", updated.DeviceLabel);
    }

    [Fact]
    public async Task TheBrowserUserAgentIsTheFallbackDeviceLabel()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/push/subscription")
        {
            Content = JsonContent.Create(Payload(Endpoint(), NewSubscriptionKeys(), keyId))
        };
        request.Headers.Add("X-Palabras", "1");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 15; OnePlus 15) Chrome/140.0");
        using var response = await learner.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.StartsWith("Mozilla/5.0 (Linux; Android 15", row.DeviceLabel, StringComparison.Ordinal);
        Assert.True(row.DeviceLabel!.Length <= 60);
    }

    [Fact]
    public async Task AStaleKeyIdIsRejectedWith409()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var current = await KeyIdAsync(learner);
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(Endpoint(), NewSubscriptionKeys(), "0000000000000000"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("stale_key", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(current, body.RootElement.GetProperty("keyId").GetString());
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http", "invalid_endpoint")]
    [InlineData("relative", "invalid_endpoint")]
    [InlineData("long", "invalid_endpoint")]
    [InlineData("loopback", "invalid_endpoint_host")]
    [InlineData("private", "invalid_endpoint_host")]
    [InlineData("ipv6-loopback", "invalid_endpoint_host")]
    [InlineData("localhost", "invalid_endpoint_host")]
    [InlineData("localhost-dot", "invalid_endpoint_host")]
    [InlineData("localhost-uppercase", "invalid_endpoint_host")]
    [InlineData("localhost-uppercase-dot", "invalid_endpoint_host")]
    [InlineData("loopback-dot", "invalid_endpoint_host")]
    [InlineData("single-label", "invalid_endpoint_host")]
    [InlineData("bad-p256dh", "invalid_p256dh")]
    [InlineData("bad-auth", "invalid_auth")]
    [InlineData("long-label", "invalid_device_label")]
    [InlineData("missing-keys", "missing_keys")]
    [InlineData("missing-key-id", "invalid_key_id")]
    [InlineData("missing-endpoint", "missing_endpoint")]
    public async Task MalformedPayloadsAreRejectedWithPreciseErrors(string shape, string error)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        var keys = NewSubscriptionKeys();
        var endpoint = Endpoint();
        var body = new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["keys"] = new { p256dh = keys.P256dh, auth = keys.Auth },
            ["keyId"] = keyId
        };
        switch (shape)
        {
            case "http":
                body["endpoint"] = endpoint.Replace("https://", "http://", StringComparison.Ordinal);
                break;
            case "relative":
                body["endpoint"] = "/fcm/send/abc";
                break;
            case "long":
                body["endpoint"] = "https://fcm.googleapis.com/" + new string('a', 1010);
                break;
            case "loopback":
                body["endpoint"] = "https://127.0.0.1/fcm/send/abc";
                break;
            case "private":
                body["endpoint"] = "https://10.1.2.3/fcm/send/abc";
                break;
            case "ipv6-loopback":
                body["endpoint"] = "https://[::1]/fcm/send/abc";
                break;
            case "localhost":
                body["endpoint"] = "https://localhost/fcm/send/abc";
                break;
            case "localhost-dot":
                body["endpoint"] = "https://localhost./fcm/send/abc";
                break;
            case "localhost-uppercase":
                body["endpoint"] = "https://LOCALHOST/fcm/send/abc";
                break;
            case "localhost-uppercase-dot":
                body["endpoint"] = "https://LOCALHOST./fcm/send/abc";
                break;
            case "loopback-dot":
                body["endpoint"] = "https://127.0.0.1./fcm/send/abc";
                break;
            case "single-label":
                body["endpoint"] = "https://intranet/fcm/send/abc";
                break;
            case "bad-p256dh":
                body["keys"] = new { p256dh = "AAAA", auth = keys.Auth };
                break;
            case "bad-auth":
                body["keys"] = new { p256dh = keys.P256dh, auth = Base64Url(new byte[15]) };
                break;
            case "long-label":
                body["deviceLabel"] = new string('a', 61);
                break;
            case "missing-keys":
                body.Remove("keys");
                break;
            case "missing-key-id":
                body.Remove("keyId");
                break;
            case "missing-endpoint":
                body.Remove("endpoint");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription", body);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, error);
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARegeneratedKeyWarnsAtStartupThatStoredSubscriptionsAreOrphaned()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new PushApplicationFactory(Now, loggerProvider: logs, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/earlier", P256dh = "p", Auth = "a",
                VapidKeyId = "0123456789abcdef", CreatedAtMs = 1, UpdatedAtMs = 1, State = "active"
            }));

        // Touching the host runs Program: a missing key file is generated and then audited.
        _ = factory.Services;

        var warning = Assert.Single(logs.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("existing subscriptions",
                StringComparison.Ordinal));
        Assert.Contains("1 existing subscriptions are bound to another key", warning.Message,
            StringComparison.Ordinal);
        Assert.Contains("current key_id", warning.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(factory.VapidPath));
        // The audit reads the private key material only through VapidKeyStore; it never logs it.
        var keys = await VapidKeyStore.ReadIfPresentAsync(factory.VapidPath, CancellationToken.None);
        Assert.NotNull(keys);
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(keys!.PrivateKey, entry.Message,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewerCannotCreateOrRemoveSubscriptions()
    {
        using var factory = new PushApplicationFactory(Now);
        using var reviewer = await LoginAsync(factory, "reviewer");
        var payload = Payload(Endpoint(), NewSubscriptionKeys(), "0000000000000000");
        await AssertErrorAsync(await SendAsync(reviewer, HttpMethod.Put, "/api/push/subscription", payload),
            HttpStatusCode.Forbidden, "forbidden");
        await AssertErrorAsync(await SendAsync(reviewer, HttpMethod.Delete, "/api/push/subscription",
            new { endpoint = "https://fcm.googleapis.com/fcm/send/x" }), HttpStatusCode.Forbidden, "forbidden");
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingXPalabrasIsRejected()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/push/subscription")
        {
            Content = JsonContent.Create(Payload(Endpoint(), NewSubscriptionKeys(), keyId))
        };
        using var response = await learner.SendAsync(request, CancellationToken.None);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "missing_x_palabras");
    }

    [Fact]
    public async Task UnsubscribeIsIdempotentAndRevokesInsteadOfDeleting()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        var keys = NewSubscriptionKeys();
        var endpoint = Endpoint();
        using (var subscribed = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(endpoint, keys, keyId)))
        {
            Assert.Equal(HttpStatusCode.OK, subscribed.StatusCode);
        }

        using (var first = await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
                   new { endpoint }))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }
        using (var second = await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
                   new { endpoint }))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Rejected, row.State);
            Assert.Null(row.LastStatus);
            // The keys stay: revocation is a state, not a delete, and the endpoint remains the
            // audit key for notification_actions and push_deliveries.
            Assert.Equal(keys.P256dh, row.P256dh);
        }

        // A plain resync must not resurrect it; only an explicit action can.
        using (var resync = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(endpoint, keys, keyId)))
        {
            await AssertErrorAsync(resync, HttpStatusCode.Conflict, "revoked");
        }
        using (var explicitAction = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(endpoint, keys, keyId, reactivate: true)))
        {
            Assert.Equal(HttpStatusCode.OK, explicitAction.StatusCode);
            using var body = await JsonAsync(explicitAction);
            Assert.Equal("active", body.RootElement.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task UnsubscribingAnotherLearnersEndpointIsANoOp()
    {
        var endpoint = Endpoint();
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 2, Endpoint = endpoint, P256dh = "p", Auth = "a", VapidKeyId = "k",
                CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");
        using var response = await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
            new { endpoint });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(2, row.UserId);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
    }

    [Fact]
    public async Task UnsubscribingAPushServiceRejectedRowAlsoRevokesIt()
    {
        var endpoint = Endpoint();
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 1, Endpoint = endpoint, P256dh = "old", Auth = "old", VapidKeyId = "old",
                CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Rejected, LastStatus = 403
            }));
        using var learner = await LoginAsync(factory, "learner");
        using (var response = await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
                   new { endpoint }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Rejected, row.State);
            Assert.Null(row.LastStatus);
        }
        // The refusal marker is gone, so the resync no longer heals it: only an explicit action can.
        var keyId = await KeyIdAsync(learner);
        using var resync = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, NewSubscriptionKeys(), keyId));
        await AssertErrorAsync(resync, HttpStatusCode.Conflict, "revoked");
    }

    [Fact]
    public async Task TheSameEndpointUnderAnotherAccountIsReassigned()
    {
        var endpoint = Endpoint();
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 2, Endpoint = endpoint, P256dh = "old", Auth = "old", VapidKeyId = "old",
                CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, NewSubscriptionKeys(), keyId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(1, row.UserId);
        Assert.Equal(keyId, row.VapidKeyId);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
    }

    [Fact]
    public async Task APushServiceRejectedRowHealsFromAnOrdinaryResync()
    {
        var endpoint = Endpoint();
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 1, Endpoint = endpoint, P256dh = "old", Auth = "old", VapidKeyId = "old",
                CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Rejected, LastStatus = 403
            }));
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, NewSubscriptionKeys(), keyId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
        Assert.Null(row.LastStatus);
    }

    [Fact]
    public async Task ReplacesEndpointRevokesTheOldSubscription()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var keyId = await KeyIdAsync(learner);
        var oldEndpoint = Endpoint();
        using (var first = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(oldEndpoint, NewSubscriptionKeys(), keyId)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        var newEndpoint = Endpoint();
        using (var second = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(newEndpoint, NewSubscriptionKeys(), keyId, replacesEndpoint: oldEndpoint)))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }
        await using var database = factory.OpenDatabase();
        var rows = await database.PushSubscriptions.AsNoTracking().OrderBy(row => row.Id)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Equal(PushSubscriptionLifecycle.Rejected, rows[0].State);
        Assert.Null(rows[0].LastStatus);
        Assert.Equal(PushSubscriptionLifecycle.Active, rows[1].State);
    }

    [Fact]
    public async Task AnOversizedBodyIsRejectedBeforeBinding()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var body = "{\"endpoint\":\"" + new string('a', 9000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/push/subscription")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await learner.SendAsync(request, CancellationToken.None);
        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    // ---- helpers ----------------------------------------------------------

    private static string Endpoint() => "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");

    private static object Payload(string endpoint, (string P256dh, string Auth) keys, string? keyId,
        string? label = null, string? replacesEndpoint = null, bool? reactivate = null) => new
    {
        endpoint,
        keys = new { p256dh = keys.P256dh, auth = keys.Auth },
        keyId,
        deviceLabel = label,
        replacesEndpoint,
        reactivate
    };

    private static (string P256dh, string Auth) NewSubscriptionKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return (Base64Url(point), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> KeyIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/push/config", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        return body.RootElement.GetProperty("keyId").GetString()!;
    }

    private static async Task<HttpClient> LoginAsync(PushApplicationFactory factory, string username)
    {
        var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username, password = Password })
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Palabras", "1");
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = await JsonAsync(response);
        Assert.Equal(error, body.RootElement.GetProperty("error").GetString());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel Level, string Message)> _entries;

            internal CapturingLogger(List<(LogLevel Level, string Message)> entries)
            {
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed class PushApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash(Password);
        private readonly string _root = Path.Combine("/tmp", "palabras-push", Guid.NewGuid().ToString("N"));
        private readonly bool _pushEnabled;
        private readonly string? _keyFileContent;
        private readonly ILoggerProvider? _loggerProvider;

        internal PushApplicationFactory(DateTimeOffset now, bool pushEnabled = true,
            Action<PalabrasDbContext>? seed = null, string? keyFileContent = null,
            ILoggerProvider? loggerProvider = null)
        {
            _pushEnabled = pushEnabled;
            _keyFileContent = keyFileContent;
            _loggerProvider = loggerProvider;
            Time = new FakeTimeProvider(now);
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            VapidPath = Path.Combine(_root, "data", "vapid.json");
            if (keyFileContent is not null)
            {
                File.WriteAllText(VapidPath, keyFileContent);
            }
            using var database = new DatabaseSettings(DatabasePath).CreateContext();
            database.Database.Migrate();
            var stamp = DateTimeOffset.UnixEpoch;
            database.Users.AddRange(
                new User
                {
                    Id = 1, Username = "learner", Role = "owner", PasswordHash = PasswordHash,
                    CreatedAt = stamp, UpdatedAt = stamp
                },
                new User
                {
                    Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = PasswordHash,
                    CreatedAt = stamp, UpdatedAt = stamp
                });
            database.SaveChanges();
            seed?.Invoke(database);
            database.SaveChanges();
        }

        internal FakeTimeProvider Time { get; }

        internal string VapidPath { get; }

        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

        internal PalabrasDbContext OpenDatabase() => new DatabaseSettings(DatabasePath).CreateContext();

        internal HttpClient CreateHttpClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://palabras.siebentower.nl"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
            builder.UseSetting(PushSettings.EnabledKey, _pushEnabled ? "true" : "false");
            builder.UseSetting(PushSettings.VapidKeyPathKey, VapidPath);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                if (_loggerProvider is not null)
                {
                    services.AddSingleton(_loggerProvider);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
