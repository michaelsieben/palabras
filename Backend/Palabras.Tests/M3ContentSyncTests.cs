using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palabras.Content.Council;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

// M3b C4: the startup sync (plan §3.4, T-13, Q-M3-7). The host must start and answer /healthz with
// any bundle state; a bad bundle is one log line, one bundle is one transaction, and a rework is
// never a side effect of a restart. Throwaway databases live under /tmp/palabras-council.
public sealed class M3ContentSyncTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", "palabras-council", "sync-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public M3ContentSyncTests()
    {
        _databasePath = Path.Combine(_root, "data", "palabras.db");
    }

    [Fact]
    public async Task StartupImportsProposeBundlesAndIsolatesABrokenOneAsync()
    {
        var council = Path.Combine(_root, "council");
        CopyFixture("valid-propose", Path.Combine(council, "a-good"));
        var broken = CopyFixture("valid-propose", Path.Combine(council, "b-broken"));
        SetRunId(broken, "01JAB3KZ8Q4VX2M7Y6T5R4S3N9");
        await File.WriteAllTextAsync(Path.Combine(broken, "candidates.jsonl"), "{not json\n");

        using var factory = new SyncFactory(_root, _databasePath, council, enabled: true);
        using var client = factory.CreateClient(NoRedirect());
        using (var healthy = await client.GetAsync("/healthz", CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        }
        await factory.WaitForSyncAsync();

        await using var database = new DatabaseSettings(_databasePath).CreateContext();
        Assert.Equal(2, await database.Words.CountAsync());
        var run = await database.CouncilRuns.SingleAsync();
        Assert.Equal("01JAB3KZ8Q4VX2M7Y6T5R4S3N2", run.RunId);
        // The refused bundle recorded no run id and no word: it is retried on the next start.
        Assert.DoesNotContain("01JAB3KZ8Q4VX2M7Y6T5R4S3N9",
            await database.CouncilRuns.Select(row => row.RunId).ToListAsync());
        // ... and it is an operator-visible log line, not an outage.
        Assert.Contains(factory.LogLines, line => line.Contains("council sync", StringComparison.Ordinal)
            && line.Contains("refused", StringComparison.Ordinal)
            && line.Contains("01JAB3KZ8Q4VX2M7Y6T5R4S3N9", StringComparison.Ordinal));
        Assert.Contains(factory.LogLines, line => line.Contains("council sync", StringComparison.Ordinal)
            && line.Contains("created=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondStartImportsNothingAsync()
    {
        var council = Path.Combine(_root, "council");
        CopyFixture("valid-propose", Path.Combine(council, "a-good"));
        int words;
        int revisions;
        using (var first = new SyncFactory(_root, _databasePath, council, enabled: true))
        {
            using var client = first.CreateClient(NoRedirect());
            await client.GetAsync("/healthz", CancellationToken.None);
            await first.WaitForSyncAsync();
        }
        await using (var database = new DatabaseSettings(_databasePath).CreateContext())
        {
            words = await database.Words.CountAsync();
            revisions = await database.WordRevisions.CountAsync();
            Assert.Equal(2, words);
        }

        using (var second = new SyncFactory(_root, _databasePath, council, enabled: true))
        {
            using var client = second.CreateClient(NoRedirect());
            await client.GetAsync("/healthz", CancellationToken.None);
            await second.WaitForSyncAsync();
        }

        await using var after = new DatabaseSettings(_databasePath).CreateContext();
        Assert.Equal(words, await after.Words.CountAsync());
        Assert.Equal(revisions, await after.WordRevisions.CountAsync());
        Assert.Equal(1, await after.CouncilRuns.CountAsync());
    }

    [Fact]
    public async Task ReworkBundleIsNeverImportedAtStartupAsync()
    {
        var council = Path.Combine(_root, "council");
        var propose = CopyFixture("valid-propose", Path.Combine(council, "a-propose"));
        using (var first = new SyncFactory(_root, _databasePath, council, enabled: true))
        {
            using var client = first.CreateClient(NoRedirect());
            await client.GetAsync("/healthz", CancellationToken.None);
            await first.WaitForSyncAsync();
        }

        // Import first, then point the sync at a rework of a word that now exists. Without the
        // propose-only guard the rework would apply here (its target is present), so the unchanged
        // rows are the observable discriminator; the log line alone would also pass on an empty DB.
        int words;
        int revisions;
        int senses;
        int runs;
        List<(int Id, int Version, string Hash)> before;
        await using (var database = new DatabaseSettings(_databasePath).CreateContext())
        {
            words = await database.Words.CountAsync();
            revisions = await database.WordRevisions.CountAsync();
            senses = await database.Senses.CountAsync();
            runs = await database.CouncilRuns.CountAsync();
            before = (await database.Words.AsNoTracking().OrderBy(word => word.Id)
                    .Select(word => new { word.Id, word.Version, word.ContentHash }).ToListAsync())
                .Select(word => (word.Id, word.Version, word.ContentHash)).ToList();
        }
        Assert.Equal(2, words);
        Assert.Equal(1, runs);

        Directory.Delete(propose, true);
        CopyFixture("valid-rework", Path.Combine(council, "b-rework"));

        using (var second = new SyncFactory(_root, _databasePath, council, enabled: true))
        {
            using var client = second.CreateClient(NoRedirect());
            using (var healthy = await client.GetAsync("/healthz", CancellationToken.None))
            {
                Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
            }
            await second.WaitForSyncAsync();
            Assert.Contains(second.LogLines, line => line.Contains("propose bundles only", StringComparison.Ordinal));
        }

        await using var after = new DatabaseSettings(_databasePath).CreateContext();
        Assert.Equal(words, await after.Words.CountAsync());
        Assert.Equal(revisions, await after.WordRevisions.CountAsync());
        Assert.Equal(senses, await after.Senses.CountAsync());
        Assert.Equal(runs, await after.CouncilRuns.CountAsync());
        var afterRows = (await after.Words.AsNoTracking().OrderBy(word => word.Id)
                .Select(word => new { word.Id, word.Version, word.ContentHash }).ToListAsync())
            .Select(word => (word.Id, word.Version, word.ContentHash)).ToList();
        Assert.Equal(before, afterRows);
        // The rework's run id was not recorded, so it is retried on a later start (through the CLI).
        Assert.DoesNotContain("01JAB4M0R7WBY3N8Z5X2C1V4B6",
            await after.CouncilRuns.Select(row => row.RunId).ToListAsync());
    }

    [Fact]
    public async Task DisabledKillSwitchImportsNothingAsync()
    {
        var council = Path.Combine(_root, "council");
        CopyFixture("valid-propose", Path.Combine(council, "a-good"));

        using var factory = new SyncFactory(_root, _databasePath, council, enabled: false);
        using var client = factory.CreateClient(NoRedirect());
        await client.GetAsync("/healthz", CancellationToken.None);
        await factory.WaitForSyncAsync();

        await using var database = new DatabaseSettings(_databasePath).CreateContext();
        Assert.Empty(await database.Words.ToListAsync());
    }

    [Fact]
    public async Task MissingDirectoryIsAHealthyNoOpAsync()
    {
        using var factory = new SyncFactory(_root, _databasePath,
            Path.Combine(_root, "no-council-directory"), enabled: true);
        using var client = factory.CreateClient(NoRedirect());
        using var response = await client.GetAsync("/healthz", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WaitForSyncAsync();
        await using var database = new DatabaseSettings(_databasePath).CreateContext();
        Assert.Empty(await database.Words.ToListAsync());
    }

    private static WebApplicationFactoryClientOptions NoRedirect() => new()
    {
        BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false, HandleCookies = false
    };

    private static void SetRunId(string bundle, string runId)
    {
        var path = Path.Combine(bundle, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest["run_id"] = runId;
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private string CopyFixture(string name, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(Path.Combine(FixtureRoot(), name)))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        return target;
    }

    private static string FixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "council", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate tools/council/fixtures.");
    }

    private sealed class SyncFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly string _databasePath;
        private readonly string _councilDirectory;
        private readonly bool _enabled;

        internal ConcurrentQueue<string> LogLines { get; } = new();

        internal SyncFactory(string root, string databasePath, string councilDirectory, bool enabled)
        {
            _root = root;
            _databasePath = databasePath;
            _councilDirectory = councilDirectory;
            _enabled = enabled;
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
        }

        internal async Task WaitForSyncAsync()
        {
            var sync = Services.GetServices<IHostedService>().OfType<ContentSyncService>().Single();
            var task = sync.ExecuteTask;
            Assert.NotNull(task);
            // The service completes its single scan; a hang here fails the test instead of the run.
            await task!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
            builder.UseSetting(ContentSyncService.DirectoryKey, _councilDirectory);
            builder.UseSetting(ContentSyncService.EnabledKey, _enabled ? "true" : "false");
            builder.ConfigureLogging(logging => logging.AddProvider(new RecordingLoggerProvider(LogLines)));
        }
    }

    // Captures the sync's own lines so T-13 can assert the bad bundle is logged and the good one
    // reports its counts. Formatted once, at log time; the queue is only read after the sync ends.
    private sealed class RecordingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(lines);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception));
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
