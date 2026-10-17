using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Palabras.Tests;

public sealed class M1AuthTests
{
    [Fact]
    public async Task LoginMeAndLogoutFollowContract()
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var anonymous = await client.GetAsync("/api/auth/me", CancellationToken.None);
        await AssertJsonError(anonymous, HttpStatusCode.Unauthorized);
        using var login = await Login(client, "learner");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(new[] { "roles", "username" }, body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("learner", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("learner", Assert.Single(body.RootElement.GetProperty("roles").EnumerateArray()).GetString());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-palabras=", cookie);
        Assert.Contains("; path=/", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; samesite=lax", cookie);
        Assert.DoesNotContain("domain=", cookie);
        var parsedCookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(cookie);
        Assert.InRange(parsedCookie.Expires!.Value - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.99), TimeSpan.FromDays(30.01));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var me = await client.GetAsync("/api/auth/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("learner", meBody.RootElement.GetProperty("username").GetString());
        client.DefaultRequestHeaders.Add("X-Palabras", "1");
        using var logout = await client.PostAsync("/api/auth/logout", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("__Host-palabras=;", Assert.Single(logout.Headers.GetValues("Set-Cookie")));
        client.DefaultRequestHeaders.Remove("Cookie");
        using var afterLogout = await client.GetAsync("/api/auth/me", CancellationToken.None);
        await AssertJsonError(afterLogout, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("learner", "wrong")]
    [InlineData("missing", "test-password")]
    [InlineData(null, null)]
    public async Task InvalidCredentialsReturn401JsonWithoutCookie(string? username, string? password)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await Login(client, username, password);
        await AssertJsonError(response, HttpStatusCode.Unauthorized);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("learner", HttpStatusCode.Forbidden)]
    [InlineData("reviewer", HttpStatusCode.OK)]
    public async Task ReviewerQueueEnforcesRoles(string? username, HttpStatusCode expected)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        if (username != null)
        {
            using var login = await Login(client, username);
            client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        }
        using var response = await client.GetAsync("/api/review/queue", CancellationToken.None);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Location"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        if (expected == HttpStatusCode.OK)
        {
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("M4", body.RootElement.GetProperty("note").GetString());
        }
        else
        {
            Assert.True(body.RootElement.TryGetProperty("error", out _));
        }
    }

    [Theory]
    [InlineData("POST", "/api")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("PUT", "/api/unknown")]
    [InlineData("DELETE", "/api/unknown")]
    [InlineData("HEAD", "/api/ping")]
    [InlineData("OPTIONS", "/api/unknown")]
    public async Task EveryNonGetApiRequestNeedsCustomHeader(string method, string path)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var missing = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var validRequest = new HttpRequestMessage(new HttpMethod(method), path);
        validRequest.Headers.Add("X-Palabras", "1");
        using var valid = await client.SendAsync(validRequest, CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.BadRequest, valid.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1, 1")]
    public async Task WrongCustomHeaderIsRejected(string value)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        client.DefaultRequestHeaders.Add("X-Palabras", value);
        using var response = await client.PostAsync("/api/auth/logout", null, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/api/unknown")]
    [InlineData("/api/unknown.json")]
    public async Task UnknownApiPathsReturn404Json(string path)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync(path, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("application/json", "{\"username\": \"learner\",")]
    [InlineData("application/json", "not json at all")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "")]
    public async Task UnparseableLoginBodyReturnsBadRequestJson(string contentType, string body)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await PostLoginBody(client, contentType, body);
        await AssertJsonError(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnparseableLoginBodyReturnsBadRequestJsonInDevelopment()
    {
        // Development raises binding failures as BadHttpRequestException; the response must stay JSON.
        using var factory = new M1ApplicationFactory(environment: "Development");
        using var client = factory.CreateHttpClient();
        using var response = await PostLoginBody(client, "application/json", "{\"username\": \"learner\",");
        await AssertJsonError(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnsupportedLoginContentTypeReturnsApiJsonInsteadOfHtml()
    {
        // A non-JSON content type never matches the endpoint, so the API fallback answers with JSON.
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await PostLoginBody(client, "text/plain", "username=learner&password=test-password");
        await AssertJsonError(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownApiMethodReturnsApiJsonInsteadOfHtml()
    {
        // PUT /api/ping matches no endpoint method, so the API fallback answers with JSON, not HTML.
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/ping");
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartupWarnsWhenNoAuthUsersAreConfigured()
    {
        using var factory = new M1ApplicationFactory(withUsers: false);
        using var client = factory.CreateHttpClient();
        using var login = await Login(client, "learner");
        await AssertJsonError(login, HttpStatusCode.Unauthorized);
        Assert.Contains(factory.Warnings, message => message.Contains("No auth users are configured", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/")]
    [InlineData("/spa/route")]
    public async Task IndexHtmlIsNeverServedStale(string path)
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync(path, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Cache-Control")));
    }

    [Fact]
    public async Task ManifestHasCorrectMimeAndCachePolicy()
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/manifest.webmanifest", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Cache-Control")));
    }

    [Fact]
    public async Task UnknownHostReturns400()
    {
        using var factory = new M1ApplicationFactory();
        using var client = factory.CreateHttpClient("https://unknown.example");
        using var response = await client.GetAsync("/healthz", CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("172.18.0.37", true)]
    [InlineData("192.168.68.2", false)]
    [InlineData("127.0.0.1", false)]
    public async Task ForwardedHeadersAreUsedOnlyFromTrustedNetwork(string peer, bool trusted)
    {
        using var factory = new M1ApplicationFactory();
        _ = factory.Server;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { username = "learner", password = "test-password" });
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "POST";
            context.Request.Path = "/api/auth/login";
            context.Request.Host = new HostString("localhost");
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.4";
            context.Request.Headers["X-Forwarded-Host"] = "palabras.siebentower.nl";
            context.Request.Headers["X-Palabras"] = "1";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }, CancellationToken.None);
        Assert.Equal(200, result.Response.StatusCode);
        Assert.Equal(trusted ? "https" : "http", result.Request.Scheme);
        Assert.Equal(trusted ? "palabras.siebentower.nl" : "localhost", result.Request.Host.Host);
        Assert.Equal(IPAddress.Parse(trusted ? "203.0.113.4" : peer), result.Connection.RemoteIpAddress);
        // SecurePolicy.Always is independent of trust; ignored headers must not weaken the cookie.
        Assert.Contains("; secure", result.Response.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData("172.18.0.37", "unknown.example", "palabras.siebentower.nl", 200)]
    [InlineData("172.18.0.37", "localhost", "unknown.example", 400)]
    [InlineData("192.168.68.2", "localhost", "unknown.example", 200)]
    [InlineData("192.168.68.2", "unknown.example", "localhost", 400)]
    public async Task HostFilterRunsAfterTrustedForwarding(string peer, string host, string forwardedHost, int expected)
    {
        using var factory = new M1ApplicationFactory();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "GET";
            context.Request.Path = "/healthz";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(host);
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        }, CancellationToken.None);
        Assert.Equal(expected, result.Response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentCanSeedPlaintextPassword()
    {
        using var factory = new M1ApplicationFactory(environment: "Development", plaintext: true);
        using var client = factory.CreateHttpClient();
        using var login = await Login(client, "learner");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public void ProductionRejectsPlaintextPassword()
    {
        using var factory = new M1ApplicationFactory(plaintext: true);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateHttpClient());
        Assert.Contains("Plaintext Auth passwords are allowed only in Development.", exception.Message);
    }

    [Fact]
    public async Task PersistedKeysValidateCookieAcrossNewAppInstance()
    {
        var keys = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string cookie;
            using (var first = new M1ApplicationFactory(keys))
            using (var client = first.CreateHttpClient())
            using (var login = await Login(client, "reviewer"))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
                Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
            }
            using var second = new M1ApplicationFactory(keys);
            using var secondClient = second.CreateHttpClient();
            secondClient.DefaultRequestHeaders.Add("Cookie", cookie);
            using var me = await secondClient.GetAsync("/api/auth/me", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(CancellationToken.None));
            Assert.Equal("reviewer", body.RootElement.GetProperty("username").GetString());
        }
        finally
        {
            if (Directory.Exists(keys))
            {
                Directory.Delete(keys, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnwritableKeyPathDoesNotBreakLoginOrSession()
    {
        var blocked = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        await File.WriteAllTextAsync(blocked, "not a directory", CancellationToken.None);
        try
        {
            using var factory = new M1ApplicationFactory(blocked);
            using var client = factory.CreateHttpClient();
            using var login = await Login(client, "learner");
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            using var me = await client.GetAsync("/api/auth/me", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string? username, string? password = "test-password")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("X-Palabras", "1");
        request.Content = JsonContent.Create(new { username, password });
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostLoginBody(HttpClient client, string contentType, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("X-Palabras", "1");
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task AssertJsonError(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Location"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(body.RootElement.TryGetProperty("error", out _));
    }

    private sealed class M1ApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));
        private readonly string _keys;
        private readonly string _environment;
        private readonly bool _plaintext;
        private readonly bool _withUsers;
        private readonly CapturingLoggerProvider _logs = new();

        internal M1ApplicationFactory(string keys = "", string environment = "Production", bool plaintext = false, bool withUsers = true)
        {
            _keys = keys;
            _environment = environment;
            _plaintext = plaintext;
            _withUsers = withUsers;
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
            File.WriteAllText(Path.Combine(_root, "wwwroot", "manifest.webmanifest"), "{\"name\":\"Palabras\"}");
        }

        internal IReadOnlyList<string> Warnings => _logs.Warnings;

        internal HttpClient CreateHttpClient(string baseAddress = "https://palabras.siebentower.nl") => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false, HandleCookies = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", _keys);
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_root, "data", "palabras.db")}");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            if (!_withUsers)
            {
                return;
            }
            var hash = new PasswordHasher<object>().HashPassword(new object(), "test-password");
            builder.UseSetting("Auth:Users:0:Username", "learner");
            builder.UseSetting("Auth:Users:0:PasswordHash", hash);
            builder.UseSetting("Auth:Users:0:Roles:0", "learner");
            builder.UseSetting("Auth:Users:1:Username", "reviewer");
            builder.UseSetting("Auth:Users:1:PasswordHash", hash);
            builder.UseSetting("Auth:Users:1:Roles:0", "reviewer");
            if (_plaintext)
            {
                builder.UseSetting("Auth:Users:0:Password", "test-password");
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _logs.Dispose();
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        internal IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return _warnings.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string message)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _provider;

            internal CapturingLogger(CapturingLoggerProvider provider)
            {
                _provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    _provider.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
