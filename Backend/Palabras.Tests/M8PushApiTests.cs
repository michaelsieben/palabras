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

    // ---- U4: delivery attempt and accounting (plan §3.4, §3.8) -----------------

    [Fact]
    public async Task ATestSendReachesEveryActiveDeviceExactlyOnce()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        var firstEndpoint = Endpoint();
        var secondEndpoint = Endpoint();
        var firstId = await SubscribeAsync(learner, firstEndpoint, label: "OnePlus 15");
        var secondId = await SubscribeAsync(learner, secondEndpoint, label: "Tab 3");

        await using (var before = factory.OpenDatabase())
        {
            Assert.Equal(0, await before.PushDeliveries.CountAsync(CancellationToken.None));
        }

        using var response = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(2, results.Length);
            Assert.Equal([firstId, secondId], results.Select(row => row.GetProperty("id").GetInt32()).ToArray());
            Assert.All(results, row =>
            {
                Assert.Equal("delivered", row.GetProperty("outcome").GetString());
                Assert.Equal(201, row.GetProperty("status").GetInt32());
            });
        }

        // One attempt per device, carrying the plan's test payload exactly: no topic, high urgency,
        // a one-minute TTL and the current VAPID key.
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("{\"v\":1,\"kind\":\"test\"}", request.Payload);
            Assert.Equal(60, request.TimeToLiveSeconds);
            Assert.Equal(PushUrgency.High, request.Urgency);
            Assert.Null(request.Topic);
        });
        Assert.Equal([firstEndpoint, secondEndpoint], transport.Requests.Select(request => request.Endpoint).ToArray());

        await using var database = factory.OpenDatabase();
        var rows = await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
            Assert.Equal(201, row.LastStatus);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), row.LastSuccessAtMs);
            Assert.Equal(0, row.FailureCount);
        });

        // A test send is not a reminder decision: push_deliveries.reminder_log_id is NOT NULL and a
        // synthetic daily/webpush reminder_log row would occupy today's claim key, so the route must
        // leave the accounting table exactly as it found it.
        Assert.Equal(0, await database.PushDeliveries.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AGoneSubscriptionIsRemovedAndTheNextSendSkipsItWithAReason()
    {
        var transport = new ScriptedPushTransport();
        transport.Answer(410);
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        var endpoint = Endpoint();
        await SubscribeAsync(learner, endpoint);

        using (var gone = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { endpoint }))
        {
            Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
            using var body = await JsonAsync(gone);
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("gone", result.GetProperty("outcome").GetString());
            Assert.Equal(410, result.GetProperty("status").GetInt32());
        }
        await using (var database = factory.OpenDatabase())
        {
            // Expiry is terminal: the row is gone, so nothing can ever send to it again, and the
            // stored bearer-like endpoint does not linger.
            Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
        }

        factory.Time.Advance(TimeSpan.FromSeconds(PushTestCooldown.WindowSeconds + 1));
        using (var skipped = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { endpoint }))
        {
            await AssertErrorAsync(skipped, HttpStatusCode.NotFound, "no_active_subscription");
        }
        // Still one attempt in total: the second send never reached the transport.
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ATransientFailureLeavesTheDeviceRetryableWithItsStatus()
    {
        var transport = new ScriptedPushTransport();
        transport.Answer(500);
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        var endpoint = Endpoint();
        await SubscribeAsync(learner, endpoint);

        using (var failed = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            using var body = await JsonAsync(failed);
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("transient", result.GetProperty("outcome").GetString());
            Assert.Equal(500, result.GetProperty("status").GetInt32());
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            // Still active: transient is not gone, so the next tick may try again — but the status
            // is recorded, because NULL means "the learner removed this device".
            Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
            Assert.Equal(500, row.LastStatus);
            Assert.Equal(1, row.FailureCount);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), row.LastFailureAtMs);
        }

        factory.Time.Advance(TimeSpan.FromSeconds(PushTestCooldown.WindowSeconds + 1));
        using (var retried = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
            Assert.Equal(0, row.FailureCount);
            Assert.Equal(201, row.LastStatus);
        }
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task ATimeoutIsRecordedWithTheNoResponseMarkerAndStaysRetryable()
    {
        var transport = new ScriptedPushTransport();
        transport.Answer(status: null, failure: "timeout");
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        await SubscribeAsync(learner, Endpoint());

        using var response = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("transient", result.GetProperty("outcome").GetString());
            Assert.Equal(PushOutcomeClassifier.NoResponseStatus, result.GetProperty("status").GetInt32());
        }
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
        Assert.Equal(PushOutcomeClassifier.NoResponseStatus, row.LastStatus);
        Assert.False(PushSubscriptionLifecycle.IsRevoked(row));
    }

    [Fact]
    public async Task ARejectedDeviceStopsSendingButCanHealFromAResync()
    {
        var transport = new ScriptedPushTransport();
        transport.Answer(403);
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        var endpoint = Endpoint();
        var keys = NewSubscriptionKeys();
        await SubscribeAsync(learner, endpoint, keys);

        using (var rejected = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { endpoint }))
        {
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            using var body = await JsonAsync(rejected);
            Assert.Equal("rejected",
                body.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("outcome")
                    .GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Rejected, row.State);
            // Non-null on purpose: a NULL status is the learner's own unsubscribe and could never
            // heal. This refusal keeps its 403, so an ordinary resync reactivates the device.
            Assert.Equal(403, row.LastStatus);
            Assert.False(PushSubscriptionLifecycle.IsRevoked(row));
        }

        factory.Time.Advance(TimeSpan.FromSeconds(PushTestCooldown.WindowSeconds + 1));
        using (var again = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            using var body = await JsonAsync(again);
            // Only `active` rows are ever sent to.
            Assert.Empty(body.RootElement.GetProperty("results").EnumerateArray());
        }
        Assert.Single(transport.Requests);

        var keyId = await KeyIdAsync(learner);
        using (var resync = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(endpoint, keys, keyId)))
        {
            Assert.Equal(HttpStatusCode.OK, resync.StatusCode);
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
            Assert.Null(row.LastStatus);
        }
    }

    [Fact]
    public async Task AStaleKeyDeviceIsMarkedAndNeverSentTo()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, transport: transport, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/old-key", P256dh = "p", Auth = "a",
                VapidKeyId = "0123456789abcdef", CreatedAtMs = 1, UpdatedAtMs = 1,
                State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");

        using var response = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("stale_key", result.GetProperty("outcome").GetString());
            Assert.False(result.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null);
        }
        Assert.Empty(transport.Requests);
        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.StaleKey, row.State);
    }

    [Fact]
    public async Task PushOffAnswers503AndNeverCallsTheTransport()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, pushEnabled: false, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }),
            HttpStatusCode.ServiceUnavailable, "push_disabled");
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task OnlyALearnerCanTriggerATestSend()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var anonymous = factory.CreateHttpClient();
        await AssertErrorAsync(await SendAsync(anonymous, HttpMethod.Post, "/api/push/test", new { }),
            HttpStatusCode.Unauthorized, "unauthorized");
        using var reviewer = await LoginAsync(factory, "reviewer");
        await AssertErrorAsync(await SendAsync(reviewer, HttpMethod.Post, "/api/push/test", new { }),
            HttpStatusCode.Forbidden, "forbidden");
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task TheSecondTestSendWithinThirtySecondsIsRateLimited()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        await SubscribeAsync(learner, Endpoint());

        using (var first = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var limited = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
            Assert.Equal("30", Assert.Single(limited.Headers.GetValues("Retry-After")));
        }
        factory.Time.Advance(TimeSpan.FromSeconds(PushTestCooldown.WindowSeconds));
        using (var after = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task NoDevicesIsAnEmptySuccessAndAnUnknownEndpointIsA404()
    {
        var transport = new ScriptedPushTransport();
        using var factory = new PushApplicationFactory(Now, transport: transport);
        using var learner = await LoginAsync(factory, "learner");
        using (var empty = await SendAsync(learner, HttpMethod.Post, "/api/push/test", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var body = await JsonAsync(empty);
            Assert.Empty(body.RootElement.GetProperty("results").EnumerateArray());
        }
        using (var unknown = await SendAsync(learner, HttpMethod.Post, "/api/push/test",
                   new { endpoint = "https://fcm.googleapis.com/fcm/send/never-registered" }))
        {
            await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "no_active_subscription");
        }
        Assert.Empty(transport.Requests);
    }

    // ---- U5: reminder settings and the action log (plan §3.5/§3.6) -------------

    [Fact]
    public async Task ReminderSettingsReadDefaultsWithoutWritingARow()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using var response = await learner.GetAsync("/api/learn/reminder", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            AssertSettings(body.RootElement, enabled: true, time: "22:00", mutedUntil: null, windowMin: 90,
                activeDevices: 0);
        }
        // "No row" means defaults: a GET must not create the row it just described.
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.ReminderSettings.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReminderSettingsRoundTripAndAPartialPutKeepsTheFieldsItOmits()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using (var put = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { time = "21:30", windowMin = 120 }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var body = await JsonAsync(put);
            AssertSettings(body.RootElement, enabled: true, time: "21:30", mutedUntil: null, windowMin: 120,
                activeDevices: 0);
        }
        await using (var database = factory.OpenDatabase())
        {
            var row = await database.ReminderSettings.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(1, row.UserId);
            Assert.True(row.Enabled);
            Assert.Equal("21:30", row.ReminderTime);
            Assert.Equal(120, row.SendWindowMin);
            Assert.Null(row.MutedUntil);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), row.UpdatedAtMs);
        }

        // Disabling alone must not reset the time or the window the first request stored.
        using (var disable = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { enabled = false }))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            using var body = await JsonAsync(disable);
            AssertSettings(body.RootElement, enabled: false, time: "21:30", mutedUntil: null, windowMin: 120,
                activeDevices: 0);
        }
        using (var get = await learner.GetAsync("/api/learn/reminder", CancellationToken.None))
        {
            using var body = await JsonAsync(get);
            AssertSettings(body.RootElement, enabled: false, time: "21:30", mutedUntil: null, windowMin: 120,
                activeDevices: 0);
        }
    }

    [Fact]
    public async Task ActiveDevicesCountsOnlyDeliverableSubscriptions()
    {
        // The app loads a pre-minted key, so a seeded row can carry the fingerprint delivery needs.
        var keyFile = PreMintedKeyFile();
        using var factory = new PushApplicationFactory(Now, keyFileContent: keyFile.Content,
            seed: database => database.PushSubscriptions.AddRange(
                new PushSubscription
                {
                    UserId = 1, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = keyFile.KeyId,
                    CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Active
                },
                new PushSubscription
                {
                    UserId = 1, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = keyFile.KeyId,
                    CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Rejected, LastStatus = 403
                },
                new PushSubscription
                {
                    // The learner's own unsubscribe: state + NULL status is the predicate (U4 ruling 3),
                    // and neither of them is a deliverable device.
                    UserId = 1, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = keyFile.KeyId,
                    CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Rejected, LastStatus = null
                },
                new PushSubscription
                {
                    // Bound to a rotated-out fingerprint: still `active`, but the sender refuses it,
                    // so counting it would claim a device that cannot ring.
                    UserId = 1, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = StaleKeyId,
                    CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Active
                }));
        using var learner = await LoginAsync(factory, "learner");

        using var response = await learner.GetAsync("/api/learn/reminder", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        AssertSettings(body.RootElement, enabled: true, time: "22:00", mutedUntil: null, windowMin: 90,
            activeDevices: 1);
    }

    [Fact]
    public async Task MuteShortcutsAreComputedInTheLearnerZone()
    {
        // 2026-10-20T12:00Z is 2026-10-21 01:00 in Auckland (+13) but still 2026-10-20 in Madrid (+2).
        using var auckland = new PushApplicationFactory(Now.AddHours(3), seed: database =>
            database.Users.Single(user => user.Id == 1).Timezone = "Pacific/Auckland");
        using var learner = await LoginAsync(auckland, "learner");

        await AssertMuteAsync(learner, "tomorrow", "2026-10-22");
        await AssertMuteAsync(learner, "3d", "2026-10-24");
        await AssertMuteAsync(learner, "7d", "2026-10-28");
        using (var disabled = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { muteChoice = "disable" }))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            using var body = await JsonAsync(disabled);
            Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Null(body.RootElement.GetProperty("mutedUntil").GetString());
        }
        // Yesterday in the learner's zone can never be a mute target, even though the same date is
        // today for the default zone.
        using (var refused = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { mutedUntil = "2026-10-20" }))
        {
            await AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_muted_until");
        }

        using var madrid = new PushApplicationFactory(Now.AddHours(3));
        using var madridLearner = await LoginAsync(madrid, "learner");
        await AssertMuteAsync(madridLearner, "tomorrow", "2026-10-21");
        using var accepted = await SendAsync(madridLearner, HttpMethod.Put, "/api/learn/reminder",
            new { mutedUntil = "2026-10-20" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedBody = await JsonAsync(accepted);
        AssertSettings(acceptedBody.RootElement, enabled: true, time: "22:00", mutedUntil: "2026-10-20",
            windowMin: 90, activeDevices: 0);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("06:15")]
    [InlineData("22:00")]
    [InlineData("23:45")]
    public async Task FifteenMinuteStepsInsideTheDayAreAccepted(string time)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using var response = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder", new { time });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal(time, body.RootElement.GetProperty("time").GetString());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(240)]
    public async Task WindowBoundsAreInclusive(int windowMin)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using var response = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder", new { windowMin });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal(windowMin, body.RootElement.GetProperty("windowMin").GetInt32());
    }

    [Theory]
    [InlineData("time", "22:10", "invalid_time")]
    [InlineData("time", "22:50", "invalid_time")]
    [InlineData("time", "24:00", "invalid_time")]
    [InlineData("time", "22:60", "invalid_time")]
    [InlineData("time", "22:75", "invalid_time")]
    [InlineData("time", "00:90", "invalid_time")]
    [InlineData("time", "-1:00", "invalid_time")]
    [InlineData("time", "12:5", "invalid_time")]
    [InlineData("time", "7:00", "invalid_time")]
    [InlineData("time", "2350", "invalid_time")]
    [InlineData("time", "evening", "invalid_time")]
    [InlineData("window", 14, "invalid_window")]
    [InlineData("window", 241, "invalid_window")]
    [InlineData("muted", "2026-10-19", "invalid_muted_until")]
    [InlineData("muted", "2026-11-20", "invalid_muted_until")]
    [InlineData("muted", "2026-02-30", "invalid_muted_until")]
    [InlineData("muted", "tomorrow", "invalid_muted_until")]
    [InlineData("mute", "soon", "invalid_mute_choice")]
    [InlineData("mute-conflict-enabled", "3d", "conflicting_mute_choice")]
    [InlineData("mute-conflict-date", "3d", "conflicting_mute_choice")]
    [InlineData("mute-conflict-enabled", "cancel", "conflicting_mute_choice")]
    [InlineData("mute-conflict-date", "cancel", "conflicting_mute_choice")]
    public async Task InvalidSettingsAreRejectedByValidationAndNothingIsStored(string shape, object value,
        string error)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        object body = shape switch
        {
            "time" => new { time = value },
            "window" => new { windowMin = value },
            "muted" => new { mutedUntil = value },
            "mute" => new { muteChoice = value },
            "mute-conflict-enabled" => new { muteChoice = value, enabled = false },
            "mute-conflict-date" => new { muteChoice = value, mutedUntil = "2026-10-25" },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        using var response = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder", body);

        // A 500 here would mean the frozen CHECK rejected the value instead of the API: validation
        // must refuse every out-of-contract value before it can reach the table.
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, error);
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.ReminderSettings.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("22:60")]
    [InlineData("22:75")]
    [InlineData("00:90")]
    [InlineData("24:00")]
    [InlineData("-1:00")]
    [InlineData("12:5")]
    [InlineData("22:1O")]
    [InlineData("")]
    public void TimeValidationRefusesImpossibleValues(string raw)
    {
        // The minute bound is its own check: a 15-minute step alone accepts "22:60", which the
        // frozen length=5 CHECK would happily store.
        Assert.False(ReminderEndpoints.TryParseTime(raw, out var normalized));
        Assert.Equal("", normalized);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("09:15")]
    [InlineData("23:45")]
    public void TimeValidationAcceptsFifteenMinuteStepsInsideTheDay(string raw)
    {
        Assert.True(ReminderEndpoints.TryParseTime(raw, out var normalized));
        Assert.Equal(raw, normalized);
    }

    [Fact]
    public async Task AnImpossibleTimeIsRefusedAndTheStoredRowIsUnchanged()
    {
        using var factory = new PushApplicationFactory(Now, seed: database => database.ReminderSettings.Add(
            new ReminderSettings
            {
                UserId = 1, Enabled = false, ReminderTime = "21:30", MutedUntil = "2026-10-22",
                SendWindowMin = 120, UpdatedAtMs = 1
            }));
        using var learner = await LoginAsync(factory, "learner");

        foreach (var time in new[] { "22:60", "24:00", "-1:00", "12:5" })
        {
            using var response = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder", new { time });
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_time");
        }

        // "Unchanged" is a property of the seeded row, not of an empty table: B1 was exactly the
        // case where an invalid value was accepted, so this asserts the stored row survived.
        await using var database = factory.OpenDatabase();
        var row = await database.ReminderSettings.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.False(row.Enabled);
        Assert.Equal("21:30", row.ReminderTime);
        Assert.Equal("2026-10-22", row.MutedUntil);
        Assert.Equal(120, row.SendWindowMin);
        Assert.Equal(1, row.UpdatedAtMs);
    }

    [Fact]
    public async Task CancelClearsAMuteInOneWriteWithoutTouchingEnabled()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using (var mute = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { muteChoice = "7d" }))
        {
            Assert.Equal(HttpStatusCode.OK, mute.StatusCode);
            using var body = await JsonAsync(mute);
            Assert.Equal("2026-10-27", body.RootElement.GetProperty("mutedUntil").GetString());
        }
        // One PUT ends the mute; no disable→enable dance that could leave reminders switched off.
        using (var cancel = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { muteChoice = "cancel" }))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            using var body = await JsonAsync(cancel);
            AssertSettings(body.RootElement, enabled: true, time: "22:00", mutedUntil: null, windowMin: 90,
                activeDevices: 0);
        }

        await using var database = factory.OpenDatabase();
        var row = await database.ReminderSettings.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Null(row.MutedUntil);
        Assert.True(row.Enabled);
    }

    [Fact]
    public async Task AMissingBodyIsRefusedOnBothWriteRoutes()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Put, "/api/learn/reminder"),
                     (HttpMethod.Post, "/api/learn/notification-action")
                 })
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Palabras", "1");
            using var response = await learner.SendAsync(request, CancellationToken.None);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "missing_body");
        }

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.ReminderSettings.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ANotificationActionIsAuditedAndLinksTodaysDecision()
    {
        using var factory = new PushApplicationFactory(Now, seed: database => database.ReminderLogs.Add(
            new ReminderLog
            {
                UserId = 1, LocalDate = "2026-10-20", Kind = "daily", Channel = "webpush", Result = "sent",
                SkipAvailable = true, CreatedAtMs = 1, UpdatedAtMs = 1
            }));
        using var learner = await LoginAsync(factory, "learner");

        using (var skip = await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action",
                   new { localDate = "2026-10-20", action = "skip", result = "ok" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, skip.StatusCode);
        }
        using (var mute = await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action",
                   new { localDate = "2026-10-20", action = "mute", result = "ok", muteChoice = "3d" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, mute.StatusCode);
        }
        // A tap the morning after names yesterday: accepted, and it links nothing because that day
        // has no decision row in this database.
        using (var late = await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action",
                   new { localDate = "2026-10-19", action = "open", result = "error" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        }

        await using var database = factory.OpenDatabase();
        var rows = await database.NotificationActions.AsNoTracking().OrderBy(row => row.Id)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(1, row.UserId);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), row.CreatedAtMs);
        });
        Assert.Equal(["skip", "mute", "open"], rows.Select(row => row.Action).ToArray());
        Assert.Equal(["2026-10-20", "2026-10-20", "2026-10-19"], rows.Select(row => row.LocalDate).ToArray());
        Assert.Equal(rows[0].ReminderLogId, rows[1].ReminderLogId);
        Assert.NotNull(rows[0].ReminderLogId);
        Assert.Null(rows[2].ReminderLogId);
        Assert.Equal("3d", rows[1].MuteChoice);
        Assert.Null(rows[0].MuteChoice);
        Assert.Equal("error", rows[2].Result);
    }

    [Theory]
    [InlineData("bad-action", "invalid_action")]
    [InlineData("bad-result", "invalid_result")]
    [InlineData("bad-date", "invalid_local_date")]
    [InlineData("future-date", "invalid_local_date")]
    [InlineData("old-date", "invalid_local_date")]
    [InlineData("bad-mute", "invalid_mute_choice")]
    public async Task MalformedNotificationActionsAreRejectedAndNothingIsAudited(string shape, string error)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var body = new Dictionary<string, object?>
        {
            ["localDate"] = "2026-10-20",
            ["action"] = "skip",
            ["result"] = "ok"
        };
        switch (shape)
        {
            case "bad-action":
                body["action"] = "dismiss";
                break;
            case "bad-result":
                body["result"] = "sent";
                break;
            case "bad-date":
                body["localDate"] = "20/10/2026";
                break;
            case "future-date":
                body["localDate"] = "2026-10-21";
                break;
            case "old-date":
                body["localDate"] = "2026-10-18";
                break;
            case "bad-mute":
                body["muteChoice"] = "forever";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        using var response = await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action", body);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, error);
        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OnlyALearnerCanReadOrChangeReminderSettings()
    {
        using var factory = new PushApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        await AssertErrorAsync(await anonymous.GetAsync("/api/learn/reminder", CancellationToken.None),
            HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await SendAsync(anonymous, HttpMethod.Put, "/api/learn/reminder",
            new { enabled = true }), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await SendAsync(anonymous, HttpMethod.Post, "/api/learn/notification-action",
            new { localDate = "2026-10-20", action = "open", result = "ok" }), HttpStatusCode.Unauthorized,
            "unauthorized");

        using var reviewer = await LoginAsync(factory, "reviewer");
        await AssertErrorAsync(await reviewer.GetAsync("/api/learn/reminder", CancellationToken.None),
            HttpStatusCode.Forbidden, "forbidden");
        await AssertErrorAsync(await SendAsync(reviewer, HttpMethod.Put, "/api/learn/reminder",
            new { enabled = false }), HttpStatusCode.Forbidden, "forbidden");
        await AssertErrorAsync(await SendAsync(reviewer, HttpMethod.Post, "/api/learn/notification-action",
            new { localDate = "2026-10-20", action = "open", result = "ok" }), HttpStatusCode.Forbidden,
            "forbidden");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.ReminderSettings.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReminderSettingsAreReadableAndWritableWhilePushIsOff()
    {
        using var factory = new PushApplicationFactory(Now, pushEnabled: false);
        using var learner = await LoginAsync(factory, "learner");

        // A preference is honest while inert: the panel reads defaults and can store 21:30 before
        // an operator enables push, without a restart in between.
        using (var get = await learner.GetAsync("/api/learn/reminder", CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var body = await JsonAsync(get);
            AssertSettings(body.RootElement, enabled: true, time: "22:00", mutedUntil: null, windowMin: 90,
                activeDevices: 0);
        }
        using (var put = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
                   new { time = "21:30", windowMin = 120 }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var body = await JsonAsync(put);
            AssertSettings(body.RootElement, enabled: true, time: "21:30", mutedUntil: null, windowMin: 120,
                activeDevices: 0);
        }
        // Only the action audit stays gated: it is produced by real notifications, and a delivery
        // that never happened must not write into that table.
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action",
            new { localDate = "2026-10-20", action = "open", result = "ok" }), HttpStatusCode.ServiceUnavailable,
            "push_disabled");

        await using var database = factory.OpenDatabase();
        var row = await database.ReminderSettings.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("21:30", row.ReminderTime);
        Assert.Equal(120, row.SendWindowMin);
        Assert.Empty(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AKeyFileFaultCannotHideStoredReminderSettings()
    {
        // push_unavailable used to gate GET as well, so an unusable VAPID file hid a stored
        // preference that no key is needed to read. Settings keep answering; only delivery is off.
        using var factory = new PushApplicationFactory(Now, keyFileContent: "{ not a key file",
            seed: database => database.ReminderSettings.Add(new ReminderSettings
            {
                UserId = 1, Enabled = false, ReminderTime = "21:30", MutedUntil = "2026-10-22",
                SendWindowMin = 120, UpdatedAtMs = 1
            }));
        using var learner = await LoginAsync(factory, "learner");

        using (var get = await learner.GetAsync("/api/learn/reminder", CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var body = await JsonAsync(get);
            AssertSettings(body.RootElement, enabled: false, time: "21:30", mutedUntil: "2026-10-22",
                windowMin: 120, activeDevices: 0);
        }
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Post, "/api/learn/notification-action",
            new { localDate = "2026-10-20", action = "open", result = "ok" }), HttpStatusCode.ServiceUnavailable,
            "push_unavailable");
    }

    [Fact]
    public async Task ReminderWritesRequireXPalabras()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/learn/reminder")
               {
                   Content = JsonContent.Create(new { time = "21:00" })
               })
        {
            using var response = await learner.SendAsync(put, CancellationToken.None);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "missing_x_palabras");
        }
        using (var action = new HttpRequestMessage(HttpMethod.Post, "/api/learn/notification-action")
               {
                   Content = JsonContent.Create(new { localDate = "2026-10-20", action = "open", result = "ok" })
               })
        {
            using var response = await learner.SendAsync(action, CancellationToken.None);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "missing_x_palabras");
        }
        // Reads are not state-changing, so the CSRF header stays optional for GET.
        using var get = await learner.GetAsync("/api/learn/reminder", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    // ---- U8: the read-only device list (plan §3.9) ----------------------------

    [Fact]
    public async Task TheDeviceListReportsEachStoredDeviceWithoutAnySecret()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var activeEndpoint = Endpoint();
        var activeKeys = NewSubscriptionKeys();
        var activeId = await SubscribeAsync(learner, activeEndpoint, activeKeys, label: "OnePlus 15");
        var revokedEndpoint = Endpoint();
        var revokedKeys = NewSubscriptionKeys();
        var revokedId = await SubscribeAsync(learner, revokedEndpoint, revokedKeys, label: "Tab 3");

        // The learner switches the tablet off: the row stays for the audit trail, as `rejected` with
        // a NULL status, which is exactly what the settings screen must display.
        using (var deleted = await SendAsync(learner, HttpMethod.Delete, "/api/push/subscription",
                   new { endpoint = revokedEndpoint }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var response = await learner.GetAsync($"/api/push/subscriptions?currentId={activeId}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken.None);
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);

        // Active devices first: the phone the learner still wants is row one, its own opt-out second.
        var first = items[0];
        Assert.Equal(activeId, first.GetProperty("id").GetInt32());
        Assert.Equal("OnePlus 15", first.GetProperty("deviceLabel").GetString());
        Assert.Equal(PushSubscriptionLifecycle.Active, first.GetProperty("state").GetString());
        Assert.True(first.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("lastStatus").ValueKind);
        Assert.Equal(Now, DateTimeOffset.Parse(first.GetProperty("createdAt").GetString()!,
            CultureInfo.InvariantCulture));
        Assert.Equal(Now, DateTimeOffset.Parse(first.GetProperty("updatedAt").GetString()!,
            CultureInfo.InvariantCulture));

        var second = items[1];
        Assert.Equal(revokedId, second.GetProperty("id").GetInt32());
        Assert.Equal("Tab 3", second.GetProperty("deviceLabel").GetString());
        Assert.Equal(PushSubscriptionLifecycle.Rejected, second.GetProperty("state").GetString());
        Assert.False(second.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("lastStatus").ValueKind);

        // The payload is secret-free: the member set is the whole contract (no endpoint, p256dh or
        // auth member exists to forget), and none of those stored values appears anywhere in the raw
        // JSON either.
        Assert.Equal(["id", "deviceLabel", "state", "lastStatus", "createdAt", "updatedAt", "isCurrent"],
            first.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(first.EnumerateObject().Select(property => property.Name).ToArray(),
            second.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.All(items, item => Assert.Equal(JsonValueKind.String, item.GetProperty("state").ValueKind));
        foreach (var secret in new[]
                 {
                     activeEndpoint, revokedEndpoint, activeKeys.P256dh, activeKeys.Auth,
                     revokedKeys.P256dh, revokedKeys.Auth
                 })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }

        // An id this account does not own is not a hint that it exists: every row is simply not current.
        using var foreign = await learner.GetAsync("/api/push/subscriptions?currentId=999999",
            CancellationToken.None);
        using var foreignBody = await JsonAsync(foreign);
        Assert.All(foreignBody.RootElement.GetProperty("items").EnumerateArray(),
            item => Assert.False(item.GetProperty("isCurrent").GetBoolean()));

        // Deterministic: the same request answers the same sequence and the same state.
        using var again = await learner.GetAsync($"/api/push/subscriptions?currentId={activeId}",
            CancellationToken.None);
        using var againBody = await JsonAsync(again);
        Assert.Equal([activeId, revokedId], againBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }

    [Fact]
    public async Task TheDeviceListNeverShowsAnotherAccountsDevices()
    {
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 2, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = StaleKeyId,
                DeviceLabel = "not mine", CreatedAtMs = 1, UpdatedAtMs = 1,
                State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");

        using var response = await learner.GetAsync("/api/push/subscriptions", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TheDeviceListAnswers200WithAnEmptyListWhilePushIsOff()
    {
        // A settings screen pairs this with GET /api/push/config: answering 503 here would make "no
        // devices" and "push is off" read the same to the UI, which is the ambiguity reviewers rejected.
        using (var disabled = new PushApplicationFactory(Now, pushEnabled: false))
        {
            using var learner = await LoginAsync(disabled, "learner");
            using var response = await learner.GetAsync("/api/push/subscriptions", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await JsonAsync(response);
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.False(body.RootElement.GetProperty("truncated").GetBoolean());
        }

        // An unusable key file is the second unavailable flavour, and a stored preference list does
        // not depend on the private key, so it is readable too.
        using (var unusable = new PushApplicationFactory(Now, keyFileContent: "{ not a key file"))
        {
            using var learner = await LoginAsync(unusable, "learner");
            using var response = await learner.GetAsync("/api/push/subscriptions", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task OnlyALearnerCanReadTheDeviceList()
    {
        using var factory = new PushApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        await AssertErrorAsync(await anonymous.GetAsync("/api/push/subscriptions", CancellationToken.None),
            HttpStatusCode.Unauthorized, "unauthorized");
        using var reviewer = await LoginAsync(factory, "reviewer");
        await AssertErrorAsync(await reviewer.GetAsync("/api/push/subscriptions", CancellationToken.None),
            HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task TheDeviceListIsCappedAndSaysWhenItTruncated()
    {
        var keyFile = PreMintedKeyFile();
        using var factory = new PushApplicationFactory(Now, keyFileContent: keyFile.Content, seed: database =>
        {
            for (var index = 1; index <= PushEndpoints.MaximumListedSubscriptions + 1; index++)
            {
                database.PushSubscriptions.Add(new PushSubscription
                {
                    UserId = 1, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = keyFile.KeyId,
                    CreatedAtMs = index, UpdatedAtMs = index, State = PushSubscriptionLifecycle.Active
                });
            }
        });
        using var learner = await LoginAsync(factory, "learner");

        using var response = await learner.GetAsync("/api/push/subscriptions", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(PushEndpoints.MaximumListedSubscriptions, items.Length);
        Assert.True(body.RootElement.GetProperty("truncated").GetBoolean());
        // Newest first, and the tiebreaker is the id: a total order, so the cap keeps a stable slice.
        Assert.Equal(Enumerable.Range(2, PushEndpoints.MaximumListedSubscriptions).Reverse(),
            items.Select(item => item.GetProperty("id").GetInt32()));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public async Task AnUnusableCurrentIdIsRefused(string currentId)
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");

        using var response = await learner.GetAsync($"/api/push/subscriptions?currentId={currentId}",
            CancellationToken.None);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_current_id");
    }

    // ---- U8: switch one stored device off by id (plan §3.9) --------------------

    [Fact]
    public async Task RevokingOneOwnDeviceByIdMarksItRejectedAndKeepsTheRowAuditable()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var endpoint = Endpoint();
        var deviceKeys = NewSubscriptionKeys();
        var id = await SubscribeAsync(learner, endpoint, deviceKeys, label: "OnePlus 15");
        factory.Time.Advance(TimeSpan.FromMinutes(5));

        using var response = await DeleteAsync(learner, $"/api/push/subscriptions/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // 204 carries no body, so there is nothing that could echo the endpoint or the keys back.
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(CancellationToken.None));

        await using (var database = factory.OpenDatabase())
        {
            var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
            Assert.Equal(PushSubscriptionLifecycle.Rejected, row.State);
            // NULL status is the learner's own opt-out; a push-service refusal always records one.
            Assert.Null(row.LastStatus);
            Assert.Equal(Now.AddMinutes(5).ToUnixTimeMilliseconds(), row.UpdatedAtMs);
            // Soft delete: endpoint and keys stay for the audit trail and a later reactivate.
            Assert.Equal(endpoint, row.Endpoint);
            Assert.Equal(deviceKeys.P256dh, row.P256dh);
        }

        // The list the settings screen reads reports the revoke as `rejected` with a NULL status,
        // and still calls the row current: the id equality is all `isCurrent` ever was.
        using var list = await learner.GetAsync($"/api/push/subscriptions?currentId={id}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var raw = await list.Content.ReadAsStringAsync(CancellationToken.None);
        using var body = JsonDocument.Parse(raw);
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetInt32());
        Assert.Equal(PushSubscriptionLifecycle.Rejected, item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastStatus").ValueKind);
        Assert.True(item.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(Now.AddMinutes(5), DateTimeOffset.Parse(item.GetProperty("updatedAt").GetString()!,
            CultureInfo.InvariantCulture));
        foreach (var secret in new[] { endpoint, deviceKeys.P256dh, deviceKeys.Auth })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RevokingTheSameDeviceTwiceIsANoOpSuccess()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var id = await SubscribeAsync(learner, Endpoint(), NewSubscriptionKeys());

        using (var first = await DeleteAsync(learner, $"/api/push/subscriptions/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }
        await using var database = factory.OpenDatabase();
        var afterFirst = (await database.PushSubscriptions.AsNoTracking()
            .SingleAsync(CancellationToken.None)).UpdatedAtMs;
        factory.Time.Advance(TimeSpan.FromMinutes(5));

        using (var second = await DeleteAsync(learner, $"/api/push/subscriptions/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
            Assert.Equal(string.Empty, await second.Content.ReadAsStringAsync(CancellationToken.None));
        }

        await using var check = factory.OpenDatabase();
        var row = await check.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.Rejected, row.State);
        Assert.Null(row.LastStatus);
        // No second event: the retry is invisible, which is what makes the call safely repeatable.
        Assert.Equal(afterFirst, row.UpdatedAtMs);
    }

    [Fact]
    public async Task RevokingAnotherAccountsDeviceAnswersTheSame404AsAMissingId()
    {
        using var factory = new PushApplicationFactory(Now, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 2, Endpoint = Endpoint(), P256dh = "p", Auth = "a", VapidKeyId = StaleKeyId,
                DeviceLabel = "not mine", CreatedAtMs = 1, UpdatedAtMs = 1,
                State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");
        int foreignId;
        await using (var database = factory.OpenDatabase())
        {
            foreignId = (await database.PushSubscriptions.AsNoTracking()
                .SingleAsync(CancellationToken.None)).Id;
        }

        using var foreign = await DeleteAsync(learner, $"/api/push/subscriptions/{foreignId}");
        var foreignRaw = await foreign.Content.ReadAsStringAsync(CancellationToken.None);
        await AssertErrorAsync(foreign, HttpStatusCode.NotFound, "subscription_not_found");

        // An id that never existed must be indistinguishable from another account's id: no oracle.
        using var missing = await DeleteAsync(learner, "/api/push/subscriptions/999999");
        var missingRaw = await missing.Content.ReadAsStringAsync(CancellationToken.None);
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, "subscription_not_found");
        Assert.Equal(missingRaw, foreignRaw);

        // The other account's row is untouched.
        await using var check = factory.OpenDatabase();
        var row = await check.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(2, row.UserId);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
        Assert.Equal(1, row.UpdatedAtMs);
    }

    [Fact]
    public async Task OnlyALearnerCanRevokeADeviceById()
    {
        using var factory = new PushApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        await AssertErrorAsync(await DeleteAsync(anonymous, "/api/push/subscriptions/1"),
            HttpStatusCode.Unauthorized, "unauthorized");
        using var reviewer = await LoginAsync(factory, "reviewer");
        await AssertErrorAsync(await DeleteAsync(reviewer, "/api/push/subscriptions/1"),
            HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task ARevokedDeviceComesBackOnlyWithAnExplicitReactivate()
    {
        using var factory = new PushApplicationFactory(Now);
        using var learner = await LoginAsync(factory, "learner");
        var endpoint = Endpoint();
        var deviceKeys = NewSubscriptionKeys();
        var keyId = await KeyIdAsync(learner);
        var id = await SubscribeAsync(learner, endpoint, deviceKeys, label: "Tab 3");
        using (var revoked = await DeleteAsync(learner, $"/api/push/subscriptions/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        // U3's contract still holds: an ordinary resync must not resurrect a device the learner
        // switched off, so the browser has to send `reactivate: true` (the explicit click).
        await AssertErrorAsync(await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                Payload(endpoint, deviceKeys, keyId, label: "Tab 3")),
            HttpStatusCode.Conflict, "revoked");

        using (var reactivated = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
                   Payload(endpoint, deviceKeys, keyId, label: "Tab 3", reactivate: true)))
        {
            Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
            using var body = await JsonAsync(reactivated);
            Assert.Equal(id, body.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(PushSubscriptionLifecycle.Active, body.RootElement.GetProperty("state").GetString());
        }

        await using var database = factory.OpenDatabase();
        var row = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
        Assert.Null(row.LastStatus);
    }

    [Fact]
    public async Task RevokingByDeviceIdIsGatedLikeEveryOtherPushWriteWhenPushIsOff()
    {
        // Rows survive a deployment that switches push off; the mutation stays gated exactly like
        // PUT/DELETE /subscription, so a frozen deployment cannot be edited through this route.
        var seeded = Endpoint();
        using var factory = new PushApplicationFactory(Now, pushEnabled: false, seed: database =>
            database.PushSubscriptions.Add(new PushSubscription
            {
                UserId = 1, Endpoint = seeded, P256dh = "p", Auth = "a", VapidKeyId = StaleKeyId,
                CreatedAtMs = 1, UpdatedAtMs = 1, State = PushSubscriptionLifecycle.Active
            }));
        using var learner = await LoginAsync(factory, "learner");
        int id;
        await using (var database = factory.OpenDatabase())
        {
            id = (await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None)).Id;
        }

        await AssertErrorAsync(await DeleteAsync(learner, $"/api/push/subscriptions/{id}"),
            HttpStatusCode.ServiceUnavailable, "push_disabled");

        await using var check = factory.OpenDatabase();
        var row = await check.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(PushSubscriptionLifecycle.Active, row.State);
        Assert.Equal(1, row.UpdatedAtMs);
    }

    // ---- helpers ----------------------------------------------------------

    // A fingerprint no generated key can have, so a row carrying it is provably undeliverable.
    private const string StaleKeyId = "0000000000000000";

    // A pre-minted VAPID key file so a seeded subscription can carry the fingerprint the running
    // app will load at startup; the app never mints a key a test could not predict.
    private static (string Content, string KeyId) PreMintedKeyFile()
    {
        var keys = VapidKeyStore.Generate(TimeProvider.System);
        var content = JsonSerializer.Serialize(new
        {
            V = 1,
            PublicKey = keys.PublicKey,
            PrivateKey = keys.PrivateKey,
            CreatedAt = keys.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
        });
        return (content, keys.KeyId);
    }

    private static async Task AssertMuteAsync(HttpClient learner, string choice, string expected)
    {
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/learn/reminder",
            new { muteChoice = choice });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal(expected, body.RootElement.GetProperty("mutedUntil").GetString());
    }

    private static void AssertSettings(JsonElement root, bool enabled, string time, string? mutedUntil,
        int windowMin, int activeDevices)
    {
        Assert.Equal(enabled, root.GetProperty("enabled").GetBoolean());
        Assert.Equal(time, root.GetProperty("time").GetString());
        Assert.Equal(mutedUntil, root.GetProperty("mutedUntil").GetString());
        Assert.Equal(windowMin, root.GetProperty("windowMin").GetInt32());
        Assert.Equal(activeDevices, root.GetProperty("activeDevices").GetInt32());
    }

    private static async Task<int> SubscribeAsync(HttpClient learner, string endpoint,
        (string P256dh, string Auth)? keys = null, string? label = null)
    {
        var keyId = await KeyIdAsync(learner);
        using var response = await SendAsync(learner, HttpMethod.Put, "/api/push/subscription",
            Payload(endpoint, keys ?? NewSubscriptionKeys(), keyId, label));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        return body.RootElement.GetProperty("id").GetInt32();
    }

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

    // DELETE by row id has no body; it still needs the X-Palabras header, which the write guard
    // checks before authentication ever runs.
    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
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
        private readonly IPushTransport? _transport;

        internal PushApplicationFactory(DateTimeOffset now, bool pushEnabled = true,
            Action<PalabrasDbContext>? seed = null, string? keyFileContent = null,
            ILoggerProvider? loggerProvider = null, IPushTransport? transport = null)
        {
            _pushEnabled = pushEnabled;
            _keyFileContent = keyFileContent;
            _loggerProvider = loggerProvider;
            _transport = transport;
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
                if (_transport is not null)
                {
                    // Nothing in a test may reach a real push service.
                    services.RemoveAll<IPushTransport>();
                    services.AddSingleton(_transport);
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
