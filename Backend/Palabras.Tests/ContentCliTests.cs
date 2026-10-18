using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

public sealed class ContentCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "palabras-content-cli-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public ContentCliTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new DatabaseSettings(Path.Combine(_directory, "data", "palabras.db"));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("content", "approve")]
    [InlineData("content", "approve", "--unknown")]
    [InlineData("content", "approve", "--all-pending", "es-nevera-01")]
    [InlineData("content", "status", "extra")]
    [InlineData("content", "retire")]
    public async Task InvalidArgumentsFailBeforeDatabaseAccess(params string[] args)
    {
        var result = await Run(args);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Usage: content", result.Error);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task ApproveKeysWritesApproveSnapshotWithoutChangingContentAndRepeatIsNoOp()
    {
        var corpusSize = await Import();
        await using var database = _settings.CreateContext();
        var before = await database.Words.AsNoTracking().SingleAsync(word => word.Key == "es-nevera-01");
        var result = await Run(["content", "approve", "es-nevera-01", "es-coger-01", "es-nevera-01"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("approved=2 unchanged=0", result.Output);
        var word = await database.Words.AsNoTracking().SingleAsync(word => word.Id == before.Id);
        Assert.Equal("approved", word.Status);
        Assert.Equal(before.Version, word.Version);
        Assert.Equal(before.ContentHash, word.ContentHash);
        Assert.Equal(before.CreatedAt, word.CreatedAt);
        var revision = await database.WordRevisions.SingleAsync(revision => revision.WordId == word.Id && revision.Action == "approve");
        Assert.Equal(word.Version, revision.Version);
        Assert.Null(revision.ActorUserId);
        Assert.Equal(word.UpdatedAt, revision.CreatedAt);
        var snapshot = JsonNode.Parse(revision.SnapshotJson)!;
        Assert.Equal("approved", snapshot["status"]!.GetValue<string>());
        Assert.Equal(word.Key, snapshot["id"]!.GetValue<string>());
        Assert.NotEmpty(snapshot["senses"]!.AsArray());
        Assert.Equal(word.UpdatedAt, DateTimeOffset.Parse(snapshot["updatedAt"]!.GetValue<string>()).ToUnixTimeMilliseconds());
        var repeat = await Run(["content", "approve", "es-nevera-01", "es-coger-01"]);
        Assert.Equal(0, repeat.Exit);
        Assert.Contains("approved=0 unchanged=2", repeat.Output);
        // one import revision per corpus file plus the two explicit approvals above
        Assert.Equal(corpusSize + 2, await database.WordRevisions.CountAsync());
        Assert.Equal(word.UpdatedAt, (await database.Words.AsNoTracking().SingleAsync(value => value.Id == word.Id)).UpdatedAt);
    }

    [Fact]
    public async Task ApproveAllSelectsOnlyPendingAndStatusReportsEveryStatusIncludingZeros()
    {
        var corpusSize = await Import();
        await using var database = _settings.CreateContext();
        var statuses = new[] { "draft", "approved", "needs_change", "rejected", "retired" };
        var exceptions = await database.Words.OrderBy(word => word.Id).Take(statuses.Length).ToListAsync();
        for (var index = 0; index < statuses.Length; index++) exceptions[index].Status = statuses[index];
        await database.SaveChangesAsync();
        var pending = corpusSize - statuses.Length;
        var before = await Run(["content", "status"]);
        Assert.Equal(0, before.Exit);
        var beforeCounts = StatusCounts(before.Output);
        Assert.All(statuses, status => Assert.Equal(1, beforeCounts[status]));
        Assert.Equal(pending, beforeCounts["pending_review"]);
        Assert.Equal(corpusSize, beforeCounts["total"]);
        Assert.Equal(beforeCounts["total"], beforeCounts.Where(pair => pair.Key != "total").Sum(pair => pair.Value));
        var result = await Run(["content", "approve", "--all-pending"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains($"approved={pending} unchanged=0", result.Output);
        var after = await Run(["content", "status"]);
        Assert.Equal(0, after.Exit);
        var afterCounts = StatusCounts(after.Output);
        Assert.Equal(corpusSize - statuses.Length + 1, afterCounts["approved"]);
        Assert.Equal(0, afterCounts["pending_review"]);
        foreach (var status in statuses.Where(status => status != "approved")) Assert.Equal(1, afterCounts[status]);
        Assert.Equal(corpusSize, afterCounts["total"]);
        Assert.Equal(afterCounts["total"], afterCounts.Where(pair => pair.Key != "total").Sum(pair => pair.Value));
        var repeat = await Run(["content", "approve", "--all-pending"]);
        Assert.Equal(0, repeat.Exit);
        Assert.Contains("approved=0 unchanged=0", repeat.Output);
        Assert.Equal(pending, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
    }

    [Fact]
    public async Task UnknownKeyFailsWithoutPartiallyApprovingKnownKeys()
    {
        var corpusSize = await Import();
        var result = await Run(["content", "approve", "es-nevera-01", "es-unknown-01"]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Unknown word key(s): es-unknown-01", result.Error);
        Assert.Contains("No words approved", result.Error);
        await using var database = _settings.CreateContext();
        Assert.Equal(corpusSize, await database.Words.CountAsync(word => word.Status == "pending_review"));
        Assert.Equal(corpusSize, await database.WordRevisions.CountAsync());
    }

    [Fact]
    public async Task RevisionWriteFailureRollsBackStatusChanges()
    {
        var corpusSize = await Import();
        await using var database = _settings.CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_approval BEFORE INSERT ON word_revisions WHEN NEW.action='approve' BEGIN SELECT RAISE(ABORT, 'forced approval failure'); END;");
        var result = await Run(["content", "approve", "--all-pending"]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("approval transaction rolled back", result.Error);
        Assert.Equal(corpusSize, await database.Words.CountAsync(word => word.Status == "pending_review"));
        Assert.Equal(corpusSize, await database.WordRevisions.CountAsync());
    }

    [Fact]
    public async Task ProcessDispatchApprovesCorpusReportsStatusAndReturnsUnknownKeyExitCode()
    {
        var corpusSize = await Import();
        var before = await RunProcess(["content", "status"]);
        Assert.Equal(0, before.Exit);
        var beforeCounts = StatusCounts(before.Output);
        Assert.Equal(corpusSize, beforeCounts["pending_review"]);
        Assert.Equal(corpusSize, beforeCounts["total"]);
        var approve = await RunProcess(["content", "approve", "--all-pending"]);
        Assert.Equal(0, approve.Exit);
        Assert.Contains($"approved={corpusSize} unchanged=0", approve.Output);
        var after = await RunProcess(["content", "status"]);
        Assert.Equal(0, after.Exit);
        var afterCounts = StatusCounts(after.Output);
        Assert.Equal(corpusSize, afterCounts["approved"]);
        Assert.Equal(0, afterCounts["pending_review"]);
        Assert.Equal(corpusSize, afterCounts["total"]);
        var repeat = await RunProcess(["content", "approve", "--all-pending"]);
        Assert.Equal(0, repeat.Exit);
        Assert.Contains("approved=0 unchanged=0", repeat.Output);
        var unknown = await RunProcess(["content", "approve", "es-unknown-01"]);
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("Unknown word key(s): es-unknown-01", unknown.Error);
        await using var database = _settings.CreateContext();
        Assert.Equal(corpusSize, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
    }

    // `content status` prints `status=count` lines plus `total=count`; parsing them lets a test assert that the
    // numbers agree with each other instead of pinning the size of a corpus that grows every batch.
    private static Dictionary<string, int> StatusCounts(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('='))
        .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));

    private async Task<int> Import()
    {
        var corpusSize = M2ContentImportTests.CorpusFileCount();
        var result = await new ContentImporter(_settings).ImportAsync(M2ContentImportTests.CorpusDirectory(), false, CancellationToken.None);
        Assert.Equal(corpusSize, result.Created);
        Assert.Empty(result.Rejected);
        return corpusSize;
    }

    private async Task<(int Exit, string Output, string Error)> Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ContentCli.RunAsync(args, _settings, output, error, CancellationToken.None);
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
