using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

public sealed class ContentImportCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "palabras-import-cli-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public ContentImportCliTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_directory, "data", "palabras.db"));
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("import", "--dry-run")]
    [InlineData("import", "words", "--unknown")]
    [InlineData("import", "words", "--dry-run", "extra")]
    public async Task InvalidArgumentsFailWithoutDatabaseSideEffects(params string[] args)
    {
        var result = await Run(args);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Usage: import", result.Error);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task MissingDirectoryFailsAndDryRunPrintsCountsWithoutCreatingDatabase()
    {
        Assert.Equal(1, (await Run(["import", Path.Combine(_directory, "missing")])).Exit);
        var dryRun = await Run(["import", M2ContentImportTests.CorpusDirectory(), "--dry-run"]);
        Assert.Equal(0, dryRun.Exit);
        Assert.Contains("Dry run: created=58 updated=0 unchanged=0 rejected=0", dryRun.Output);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task CliImportsThenReportsUnchangedAndMalformedBatchExitsOneWithoutWrites()
    {
        var first = await Run(["import", M2ContentImportTests.CorpusDirectory()]);
        Assert.Equal(0, first.Exit);
        Assert.Contains("created=58 updated=0 unchanged=0 rejected=0", first.Output);
        var repeat = await Run(["import", M2ContentImportTests.CorpusDirectory()]);
        Assert.Equal(0, repeat.Exit);
        Assert.Contains("created=0 updated=0 unchanged=58 rejected=0", repeat.Output);
        var words = Path.Combine(_directory, "words");
        Directory.CreateDirectory(words);
        var source = M2ContentImportTests.Seed();
        source["notes"] = "should not be written";
        await File.WriteAllTextAsync(Path.Combine(words, "es-nevera-01.json"), source.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(words, "zzz-bad.json"), "{broken");
        var malformed = await Run(["import", words]);
        Assert.Equal(1, malformed.Exit);
        Assert.Contains("created=0 updated=0 unchanged=0 rejected=1", malformed.Output);
        Assert.Contains("zzz-bad.json", malformed.Error);
        await using var database = _settings.CreateContext();
        Assert.Equal(58, await database.Words.CountAsync());
        Assert.Equal(58, await database.WordRevisions.CountAsync());
        Assert.DoesNotContain(await database.Words.ToListAsync(), word => word.Notes == "should not be written");
    }

    [Fact]
    public async Task DatabaseFailureAfterFirstWordRollsBackEntireBatch()
    {
        await DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None);
        await using var database = _settings.CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_second_word BEFORE INSERT ON words WHEN NEW.key='es-nevera-01' BEGIN SELECT RAISE(ABORT, 'forced import failure'); END;");
        var words = Path.Combine(_directory, "rollback");
        Directory.CreateDirectory(words);
        var source = M2ContentImportTests.Seed();
        source["id"] = "es-first-01";
        await File.WriteAllTextAsync(Path.Combine(words, "es-first-01.json"), source.ToJsonString());
        source["id"] = "es-nevera-01";
        await File.WriteAllTextAsync(Path.Combine(words, "es-nevera-01.json"), source.ToJsonString());
        var result = await Run(["import", words]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("transaction rolled back", result.Error);
        Assert.Empty(await database.Words.ToListAsync());
        Assert.Empty(await database.Senses.ToListAsync());
        Assert.Empty(await database.WordRevisions.ToListAsync());
    }

    [Fact]
    public async Task ProgramDispatchUsesEnvironmentDatabaseAndActualProcessExitCodes()
    {
        var first = await RunProcess(["import", M2ContentImportTests.CorpusDirectory()]);
        Assert.Equal(0, first.Exit);
        Assert.Contains("created=58 updated=0 unchanged=0 rejected=0", first.Output);
        var repeat = await RunProcess(["import", M2ContentImportTests.CorpusDirectory()]);
        Assert.Equal(0, repeat.Exit);
        Assert.Contains("created=0 updated=0 unchanged=58 rejected=0", repeat.Output);
        var words = Path.Combine(_directory, "broken");
        Directory.CreateDirectory(words);
        await File.WriteAllTextAsync(Path.Combine(words, "bad.json"), "{");
        var malformed = await RunProcess(["import", words]);
        Assert.Equal(1, malformed.Exit);
        Assert.Contains("rejected=1", malformed.Output);
        Assert.Contains("bad.json", malformed.Error);
        await using var database = _settings.CreateContext();
        Assert.Equal(58, await database.Words.CountAsync());
        Assert.Equal(58, await database.WordRevisions.CountAsync());
    }

    private async Task<(int Exit, string Output, string Error)> Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ContentImportCli.RunAsync(args, _settings, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> RunProcess(string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["DATA_DIR"] = Path.GetDirectoryName(_settings.FilePath)!;
        info.Environment["ConnectionStrings__Default"] = "Data Source=" + _settings.FilePath;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
