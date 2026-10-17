using System.Net;
using System.Text.Json;
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
    public async Task PingReturnsDocumentedStatusAndVersionJsonShape()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();

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
    public async Task UnknownApiRouteReturnsNotFoundInsteadOfSpaDocument()
    {
        using var factory = new M0ApplicationFactory();
        using var client = factory.CreateHttpClient();

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
        using var client = factory.CreateHttpClient();

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
        using var client = factory.CreateHttpClient("https://palabras.example.test");

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal("https", response.RequestMessage?.RequestUri?.Scheme);
        Assert.Equal("palabras.example.test", response.RequestMessage?.RequestUri?.Host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task HealthzReturnsOkWhenDataDirectoryDoesNotExist()
    {
        using var factory = new M0ApplicationFactory();
        // Verify the literal deployment path is absent too; never delete host data.
        Assert.False(Directory.Exists(Path.GetFullPath("/data")), "Test environment must not mount /data.");
        Assert.False(Directory.Exists(factory.MissingDataDirectory));
        using var client = factory.CreateHttpClient();

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(factory.MissingDataDirectory));
    }

    private sealed class M0ApplicationFactory : WebApplicationFactory<Program>
    {
        internal const string SpaMarker = "palabras-m0-test-spa";
        internal const string SpaHtml = "<!doctype html><html><body><div id=\"palabras-m0-test-spa\"></div></body></html>";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));

        internal string MissingDataDirectory => Path.Combine(_root, "data");

        internal M0ApplicationFactory()
        {
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), SpaHtml);
        }

        internal HttpClient CreateHttpClient(string baseAddress = "http://localhost") => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Exercise production middleware with an isolated SPA and an absent data directory.
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(MissingDataDirectory, "palabras.db")}");
            builder.UseSetting("https_port", "443");
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
