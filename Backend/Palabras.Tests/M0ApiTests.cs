using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Palabras.Accounts;
using Palabras.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Palabras.Tests;

public sealed class M0ApiTests
{
    [Fact]
    public async Task HealthzReturnsOkStatusAndNonEmptyVersion()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task HealthzReturnsServiceUnavailableWhenDatabaseIsUnreachable()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();
        using (var healthy = await client.GetAsync("/healthz", CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        }

        // Simulate the incident without touching startup: the file existed when the app booted and then
        // vanished. A probe that opens with ReadWriteCreate would recreate it empty and answer 200, so
        // this deletes the file exactly as the incident did and asserts it stays gone.
        File.Delete(factory.DatabasePath);
        File.Delete(factory.DatabasePath + "-wal");
        File.Delete(factory.DatabasePath + "-shm");
        Assert.False(File.Exists(factory.DatabasePath));

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        using var body = JsonDocument.Parse(content);
        Assert.Equal("unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("database", body.RootElement.GetProperty("component").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));
        // No filesystem path or exception detail may reach the unauthenticated caller.
        Assert.DoesNotContain(factory.DatabasePath, content);
        Assert.DoesNotContain("Sqlite", content, StringComparison.OrdinalIgnoreCase);
        // The probe must not have a write side effect: reporting unhealthy may not recreate the database.
        Assert.False(File.Exists(factory.DatabasePath));
    }

    [Fact]
    public async Task PingReturnsDocumentedStatusAndVersionJsonShape()
    {
        using var factory = new M0ApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        using var response = await client.GetAsync("/api/ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(new[] { "status", "version" },
            body.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task PwaShellFilesReturnNoCacheWhileHashedAssetsKeepLongLivedCaching()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();

        using var worker = await client.GetAsync("/sw.js", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, worker.StatusCode);
        Assert.Equal("no-cache", Assert.Single(worker.Headers.GetValues("Cache-Control")));

        using var manifest = await client.GetAsync("/manifest.webmanifest", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("no-cache", Assert.Single(manifest.Headers.GetValues("Cache-Control")));

        using var hashedAsset = await client.GetAsync("/assets/" + M0ApplicationFactory.HashedAssetName, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, hashedAsset.StatusCode);
        Assert.False(hashedAsset.Headers.TryGetValues("Cache-Control", out var assetCacheControl)
            && assetCacheControl.Any(value => value.Contains("no-cache", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task UnknownApiRouteReturnsNotFoundInsteadOfSpaDocument()
    {
        using var factory = new M0ApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        using var response = await client.GetAsync("/api/does-not-exist", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(M0ApplicationFactory.SpaMarker,
            await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClientRouteReturnsSpaHtml()
    {
        using var factory = new M0ApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        using var response = await client.GetAsync("/some-spa/route", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(M0ApplicationFactory.SpaHtml,
            await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PlainHttpRequestDoesNotRedirectToHttps()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal("http", response.RequestMessage?.RequestUri?.Scheme);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task HttpsRequestDoesNotEmitHstsHeader()
    {
        using var factory = new M0ApplicationFactory();
        // UseHsts() only emits on HTTPS requests to a host outside HstsOptions.ExcludedHosts, so this
        // exact request shape is what makes the assertion meaningful: adding UseHsts() would now fail it.
        // The scheme/host assertions keep it from going vacuous again if the base address is reverted.
        using var client = factory.CreateHttpClient("https://palabras.siebentower.nl");

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal("https", response.RequestMessage?.RequestUri?.Scheme);
        Assert.Equal("palabras.siebentower.nl", response.RequestMessage?.RequestUri?.Host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task StartupCreatesDatabaseWhenDataDirectoryDoesNotExist()
    {
        using var factory = new M0ApplicationFactory();
        Assert.False(Directory.Exists(factory.MissingDataDirectory));
        using var client = factory.CreateHttpClient();

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(factory.MissingDataDirectory, "palabras.db")));
    }

    private sealed class M0ApplicationFactory : WebApplicationFactory<Program>
    {
        internal const string SpaMarker = "palabras-m0-test-spa";
        internal const string SpaHtml = "<!doctype html><html><body><div id=\"palabras-m0-test-spa\"></div></body></html>";
        internal const string HashedAssetName = "index-B8d9Qw2x.js";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));

        internal string MissingDataDirectory => Path.Combine(_root, "data");
        internal string DatabasePath => Path.Combine(MissingDataDirectory, "palabras.db");

        internal M0ApplicationFactory()
        {
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "assets"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), SpaHtml);
            File.WriteAllText(Path.Combine(_root, "wwwroot", "sw.js"), "self.addEventListener('install', () => {});");
            File.WriteAllText(Path.Combine(_root, "wwwroot", "manifest.webmanifest"), "{\"name\":\"Palabras\"}");
            File.WriteAllText(Path.Combine(_root, "wwwroot", "assets", HashedAssetName), "export {};");
        }

        internal HttpClient CreateHttpClient(string baseAddress = "http://localhost") => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false, HandleCookies = false
        });

        internal async Task<HttpClient> CreateAuthenticatedClientAsync()
        {
            var client = CreateHttpClient();
            await using var database = new DatabaseSettings(Path.Combine(MissingDataDirectory, "palabras.db")).CreateContext();
            await new AccountService(database, new AccountPasswordHasher()).CreateAsync("owner", "learner", "test-password", CancellationToken.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Headers.Add("X-Palabras", "1");
            request.Content = JsonContent.Create(new { username = "learner", password = "test-password" });
            using var login = await client.SendAsync(request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Exercise production middleware with an isolated SPA and an absent data directory.
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(MissingDataDirectory, "palabras.db")}");
            builder.UseSetting("https_port", "443");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
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
