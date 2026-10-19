using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Content.Council;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

// M3b C4: the `import-council` verb (plan §3.2, T-14/T-15). Throwaway databases live under
// /tmp/palabras-council, never /data; a dry run must not create the file at all.
public sealed class M3CouncilCliTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", "palabras-council", Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public M3CouncilCliTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_root, "data", "palabras.db"));
    }

    [Fact]
    public async Task ProposeBundleImportsAndPrintsTheSummaryLineAsync()
    {
        var bundle = CopyFixture("valid-propose");

        var result = await Run(["import-council", bundle]);

        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Error);
        Assert.Contains("bundles=1 imported=1 created=2 skipped=0 failed=0", result.Output);
        Assert.Contains("mode=propose created=2 updated=0 unchanged=0", result.Output);
        await using var database = _settings.CreateContext();
        Assert.Equal(2, await database.Words.CountAsync());
        Assert.Equal(1, await database.CouncilRuns.CountAsync());
        Assert.All(await database.Words.ToListAsync(), word => Assert.Equal("pending_review", word.Status));
    }

    [Fact]
    public async Task DryRunPredictsCountsWithoutCreatingADatabaseAsync()
    {
        var result = await Run(["import-council", CopyFixture("valid-propose"), "--dry-run"]);

        Assert.Equal(0, result.Exit);
        Assert.Contains("Dry run: bundles=1 imported=1 created=2 skipped=0 failed=0", result.Output);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task InvalidManifestIsRefusedEvenInADryRunAsync()
    {
        var result = await Run(["import-council", CopyFixture("bad-manifest"), "--dry-run"]);

        Assert.Equal(1, result.Exit);
        Assert.Contains("Error:", result.Error, StringComparison.Ordinal);
        Assert.Contains("manifest.json", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task MissingArgumentsUnknownFlagsAndAMissingDirectoryExitOneWithoutDatabaseAsync()
    {
        var bundle = CopyFixture("valid-propose");
        string[][] invocations =
        [
            ["import-council"],
            ["import-council", "--dry-run"],
            ["import-council", bundle, "--unknown"],
            ["import-council", bundle, "--dry-run", "extra"],
            ["import-council", Path.Combine(_root, "missing")]
        ];

        foreach (var arguments in invocations)
        {
            var result = await Run(arguments);

            Assert.Equal(1, result.Exit);
            Assert.Contains("Error:", result.Error, StringComparison.Ordinal);
        }
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task HashMismatchExitsOneAndWritesNothingAsync()
    {
        var result = await Run(["import-council", CopyFixture("hash-mismatch")]);

        Assert.Equal(1, result.Exit);
        Assert.Contains("candidates_sha256", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task ParentDirectoryImportsGoodBundlesAndReportsEachBadOneAsync()
    {
        CopyFixture("valid-propose", Path.Combine(_root, "bundles", "a-good"));
        CopyFixture("hash-mismatch", Path.Combine(_root, "bundles", "b-bad"));

        var result = await Run(["import-council", Path.Combine(_root, "bundles")]);

        Assert.Equal(1, result.Exit);
        Assert.Contains("bundles=2 imported=1 created=2", result.Output);
        Assert.Contains("failed=1", result.Output);
        Assert.Contains("candidates_sha256", result.Error, StringComparison.Ordinal);
        await using var database = _settings.CreateContext();
        Assert.Equal(2, await database.Words.CountAsync());
        Assert.Equal(1, await database.CouncilRuns.CountAsync());
    }

    [Fact]
    public async Task ReimportingTheSameBundleIsANoOpAsync()
    {
        var bundle = CopyFixture("valid-propose");
        Assert.Equal(0, (await Run(["import-council", bundle])).Exit);
        int revisions;
        await using (var database = _settings.CreateContext())
        {
            revisions = await database.WordRevisions.CountAsync();
        }

        var repeat = await Run(["import-council", bundle]);

        Assert.Equal(0, repeat.Exit);
        Assert.Contains("bundles=1 imported=1 created=0 skipped=0 failed=0", repeat.Output);
        Assert.Contains("already imported", repeat.Output);
        await using var after = _settings.CreateContext();
        Assert.Equal(2, await after.Words.CountAsync());
        Assert.Equal(revisions, await after.WordRevisions.CountAsync());
    }

    [Fact]
    public async Task ReworkImportsOnlyThroughTheCliAsync()
    {
        Assert.Equal(0, (await Run(["import-council", CopyFixture("valid-propose")])).Exit);

        var rework = await Run(["import-council", CopyFixture("valid-rework")]);

        Assert.Equal(0, rework.Exit);
        // The fixture's content matches the propose bundle, so the CLI records the rework run but
        // leaves the word untouched (§3.3(1)); the point here is that it is not refused like the
        // startup sync would refuse it.
        Assert.Contains("mode=rework created=0 updated=0 unchanged=1", rework.Output);
        await using var database = _settings.CreateContext();
        var nevera = await database.Words.SingleAsync(word => word.Key == "es-nevera-01");
        Assert.Equal(1, nevera.Version);
        Assert.Equal("pending_review", nevera.Status);
        Assert.Equal(2, await database.CouncilRuns.CountAsync());
    }

    [Fact]
    public async Task ProgramDispatchRunsTheVerbAgainstTheConfiguredDatabaseAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("import-council");
        info.ArgumentList.Add(bundle);
        info.Environment["DATA_DIR"] = Path.GetDirectoryName(_settings.FilePath)!;
        info.Environment["ConnectionStrings__Default"] = "Data Source=" + _settings.FilePath;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("created=2", await output);
        Assert.Equal("", await error);
        await using var database = _settings.CreateContext();
        Assert.Equal(2, await database.Words.CountAsync());
    }

    private async Task<(int Exit, string Output, string Error)> Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CouncilImportCli.RunAsync(args, _settings, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private string CopyFixture(string name, string? target = null)
    {
        target ??= Path.Combine(_root, "bundles", name + "-" + Guid.NewGuid().ToString("N"));
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
