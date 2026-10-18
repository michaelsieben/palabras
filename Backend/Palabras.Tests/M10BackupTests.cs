using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Accounts;
using Palabras.Backup;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

public sealed class M10BackupTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    [Fact]
    public async Task VacuumBackupOpensAndIncludesCommittedWalDataAndAccounts()
    {
        using var fixture = new DatabaseFixture();
        var service = new BackupService(fixture.Settings, new FakeTimeProvider(Now), NullLogger<BackupService>.Instance);
        var backup = await service.CreateBackupAsync(CancellationToken.None);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        foreach (var table in new[] { "users", "words", "senses", "cards", "review_log", "user_settings", "__EFMigrationsHistory" })
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$table", table);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        command.CommandText = "SELECT COUNT(*) FROM review_log;";
        command.Parameters.Clear();
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        Assert.Empty(Directory.GetFiles(service.BackupDirectory, "*.partial"));
    }

    [Theory]
    [InlineData("2026-10-24T00:00:00Z", "2026-10-24T01:30:00Z", null)]
    [InlineData("2026-10-25T00:00:00Z", "2026-10-25T02:30:00Z", "Europe/Madrid")]
    [InlineData("2027-03-28T00:00:00Z", "2027-03-28T01:30:00Z", "Europe/Madrid")]
    [InlineData("2026-10-24T12:00:00Z", "2026-10-24T18:30:00Z", "Asia/Tokyo")]
    public async Task HostedScheduleFiresAt0330LocalUsingFakeTime(string start, string expected, string? zone)
    {
        using var fixture = new DatabaseFixture();
        await using (var database = fixture.Settings.CreateContext())
        {
            await database.Users.Where(user => user.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Timezone, zone));
        }
        var time = new ObservedTimeProvider(DateTimeOffset.Parse(start));
        var logger = new CompletionLogger();
        using var service = new BackupService(fixture.Settings, time, logger);
        await service.StartAsync(CancellationToken.None);
        var due = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DateTimeOffset.Parse(expected) - time.GetUtcNow(), due);
        time.Fake.Advance(due - TimeSpan.FromMilliseconds(1));
        Assert.False(logger.Published.Task.IsCompleted);
        time.Fake.Advance(TimeSpan.FromMilliseconds(1));
        var file = await logger.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(file));
        Assert.Equal(DateTimeOffset.Parse(expected), time.GetUtcNow());
        var followingDelay = await time.SecondTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BackupService.NextRun(time.GetUtcNow().AddTicks(1), zone) - time.GetUtcNow(), followingDelay);
        await service.StopAsync(CancellationToken.None);
        Assert.Single(Directory.GetFiles(service.BackupDirectory, "*.db"));
    }

    [Theory]
    [InlineData("2026-10-24T01:30:00Z", "2026-10-25T02:30:00Z", 25)]
    [InlineData("2027-03-27T02:30:00Z", "2027-03-28T01:30:00Z", 23)]
    public void ConsecutiveLocalNightsAreNotAlways24Hours(string previous, string expected, int hours)
    {
        var before = DateTimeOffset.Parse(previous);
        var next = BackupService.NextRun(before.AddTicks(1), "Europe/Madrid");
        Assert.Equal(DateTimeOffset.Parse(expected), next);
        Assert.Equal(TimeSpan.FromHours(hours), next - before);
    }

    [Fact]
    public async Task RetentionKeepsSevenAndNeverDeletesNewestOrUnrelatedFiles()
    {
        using var fixture = new DatabaseFixture();
        var time = new FakeTimeProvider(Now);
        var service = new BackupService(fixture.Settings, time, NullLogger<BackupService>.Instance);
        var paths = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            paths.Add(await service.CreateBackupAsync(CancellationToken.None));
            time.Advance(TimeSpan.FromDays(1));
        }
        Assert.All(paths.Take(3), path => Assert.False(File.Exists(path)));
        Assert.All(paths.Skip(3), path => Assert.True(File.Exists(path)));
        var unrelated = Path.Combine(service.BackupDirectory, "operator-copy.db");
        await File.WriteAllTextAsync(unrelated, "operator backup");
        var newest = await service.CreateBackupAsync(CancellationToken.None);
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(7, Directory.GetFiles(service.BackupDirectory, "palabras-*.db").Length);
    }

    [Fact]
    public async Task FailedHostedBackupLogsAndKeepsPreviousSnapshotWithoutKillingService()
    {
        using var fixture = new DatabaseFixture();
        var time = new ObservedTimeProvider(DateTimeOffset.Parse("2026-10-25T00:00:00Z"));
        var logger = new CompletionLogger();
        using var service = new BackupService(fixture.Settings, time, logger);
        var previous = await service.CreateBackupAsync(CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(previous);
        // Missing source: VACUUM on an empty file cannot complete the schema query in the hosted loop.
        File.Move(fixture.Settings.FilePath, fixture.Settings.FilePath + ".saved");
        await service.StartAsync(CancellationToken.None);
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(previous));
        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(service.BackupDirectory, "*.partial"));
    }

    [Fact]
    public async Task FailedSnapshotPublicationDeletesPartialAndPreservesPreviousBackup()
    {
        using var fixture = new DatabaseFixture();
        var service = new BackupService(fixture.Settings, new FakeTimeProvider(Now), NullLogger<BackupService>.Instance);
        var previous = await service.CreateBackupAsync(CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(previous);
        File.Move(fixture.Settings.FilePath, fixture.Settings.FilePath + ".saved");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync(CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(previous));
        Assert.Empty(Directory.GetFiles(service.BackupDirectory, "*.partial"));
        Assert.Single(Directory.GetFiles(service.BackupDirectory, "*.db"));
    }

    [Fact]
    public async Task ProgramDispatchesRestoreVerbAndMalformedFileExitsNonzero()
    {
        using var fixture = new DatabaseFixture();
        var export = await ProgressFiles.ExportAsync(fixture.Settings, 1, new FakeTimeProvider(Now), CancellationToken.None);
        var file = Path.Combine(fixture.Root, "progress.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(export, ProgressFiles.Json));
        Assert.Equal(0, await RunProcess(fixture.Settings, file));
        var before = await AllRows(fixture.Settings);
        await File.WriteAllTextAsync(file, "{malformed");
        Assert.Equal(1, await RunProcess(fixture.Settings, file));
        Assert.Equal(before, await AllRows(fixture.Settings));
    }

    [Fact]
    public async Task ExportIsScopedAndRoundTripsAfterSnapshotRestoreAndLogin()
    {
        using var fixture = new DatabaseFixture();
        using var factory = new ExportFactory(fixture);
        using var client = await LoginClient(factory, "learner");
        // Baseline snapshot contains credentials/content but not the progress being exported later.
        await using (var database = fixture.Settings.CreateContext())
        {
            await database.ReviewLogs.Where(review => review.UserId == 1).ExecuteDeleteAsync();
            await database.Cards.Where(card => card.UserId == 1).ExecuteDeleteAsync();
        }
        var backupService = new BackupService(fixture.Settings, new FakeTimeProvider(Now), NullLogger<BackupService>.Instance);
        var baseline = await backupService.CreateBackupAsync(CancellationToken.None);
        await using (var database = fixture.Settings.CreateContext())
        {
            database.Cards.Add(DatabaseFixture.Card(1, 1));
            await database.SaveChangesAsync();
            database.ReviewLogs.Add(DatabaseFixture.Review(1, 1));
            await database.SaveChangesAsync();
        }
        using var response = await client.GetAsync("/api/learn/export?format=json&userId=2", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var export = ProgressFiles.Parse(text);
        Assert.Equal(1, export.UserId);
        Assert.Single(export.Cards);
        Assert.Single(export.ReviewLog);
        Assert.All(export.Cards, card => Assert.Equal(1, card.UserId));
        Assert.All(export.ReviewLog, review => Assert.Equal(1, review.UserId));
        Assert.DoesNotContain("passwordHash", text);
        Assert.DoesNotContain("reviewer-only-secret", text);
        var import = Path.Combine(fixture.Root, "progress.json");
        await File.WriteAllTextAsync(import, text);

        // Restore into a separate offline copy, never replace a file while its server is running.
        using var restored = new DatabaseFixture(seed: false);
        File.Copy(baseline, restored.Settings.FilePath);
        Assert.Equal(0, await RunProcess(restored.Settings, import));
        Assert.Equal(0, await RestoreProgressCli.RunAsync(["restore-progress", import], restored.Settings,
            TextWriter.Null, TextWriter.Null, CancellationToken.None));
        await using (var database = restored.Settings.CreateContext())
        {
            Assert.Equal(2, await database.Cards.CountAsync());
            Assert.Equal(2, await database.ReviewLogs.CountAsync());
            Assert.Equal(3, (await database.Cards.SingleAsync(card => card.Id == 1)).Reps);
            Assert.Equal("reviewer-only-secret", (await database.ReviewLogs.SingleAsync(review => review.Id == 2)).AnswerText);
        }
        using var restoredFactory = new ExportFactory(restored);
        using var loggedIn = await LoginClient(restoredFactory, "learner");
        using var roundTrip = await loggedIn.GetAsync("/api/learn/export?format=json", CancellationToken.None);
        var after = ProgressFiles.Parse(await roundTrip.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(JsonSerializer.Serialize(export.Cards), JsonSerializer.Serialize(after.Cards));
        Assert.Equal(JsonSerializer.Serialize(export.ReviewLog), JsonSerializer.Serialize(after.ReviewLog));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("missing-field")]
    [InlineData("unknown-field")]
    [InlineData("bad-rating")]
    [InlineData("foreign-user")]
    [InlineData("duplicate-card")]
    [InlineData("missing-card")]
    [InlineData("bad-date")]
    [InlineData("card-conflict")]
    [InlineData("review-conflict")]
    [InlineData("bad-fingerprint")]
    public async Task InvalidImportReturnsNonzeroWithZeroWrites(string kind)
    {
        using var fixture = new DatabaseFixture();
        var export = await ProgressFiles.ExportAsync(fixture.Settings, 1, new FakeTimeProvider(Now), CancellationToken.None);
        var root = JsonNode.Parse(JsonSerializer.Serialize(export, ProgressFiles.Json))!.AsObject();
        switch (kind)
        {
            case "missing-field": root["cards"]![0]!.AsObject().Remove("reps"); break;
            case "unknown-field": root["extra"] = 1; break;
            case "bad-rating": root["reviewLog"]![0]!["rating"] = 9; break;
            case "foreign-user": root["cards"]![0]!["userId"] = 2; break;
            case "duplicate-card": root["cards"]!.AsArray().Add(root["cards"]![0]!.DeepClone()); break;
            case "missing-card": root["cards"]!.AsArray().Clear(); break;
            case "bad-date": root["cards"]![0]!["buriedUntilLocalDate"] = "2026-02-30"; break;
            case "card-conflict": root["cards"]![0]!["id"] = 2; root["reviewLog"]![0]!["cardId"] = 2; break;
            case "review-conflict": root["reviewLog"]![0]!["answerText"] = "changed"; break;
            case "bad-fingerprint": root["targetFingerprint"] = new string('0', 64); break;
        }
        var before = await AllRows(fixture.Settings);
        var path = Path.Combine(fixture.Root, "invalid.json");
        await File.WriteAllTextAsync(path, kind == "truncated" ? "{" : root.ToJsonString());
        using var errors = new StringWriter();
        Assert.Equal(1, await RestoreProgressCli.RunAsync(["restore-progress", path], fixture.Settings,
            TextWriter.Null, errors, CancellationToken.None));
        Assert.Contains("Restore refused:", errors.ToString());
        Assert.Equal(before, await AllRows(fixture.Settings));
    }

    [Fact]
    public async Task UnrelatedOrMissingDatabaseIsRefusedWithoutCreatingOrChangingIt()
    {
        using var source = new DatabaseFixture();
        using var unrelated = new DatabaseFixture();
        await using (var database = unrelated.Settings.CreateContext())
        {
            await database.Users.Where(user => user.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.CreatedAt, Now.AddYears(-1)));
        }
        var export = await ProgressFiles.ExportAsync(source.Settings, 1, new FakeTimeProvider(Now), CancellationToken.None);
        var path = Path.Combine(source.Root, "progress.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, ProgressFiles.Json));
        var before = await AllRows(unrelated.Settings);
        Assert.Equal(1, await RestoreProgressCli.RunAsync(["restore-progress", path], unrelated.Settings,
            TextWriter.Null, TextWriter.Null, CancellationToken.None));
        Assert.Equal(before, await AllRows(unrelated.Settings));
        var missing = new DatabaseSettings(Path.Combine(unrelated.Root, "missing.db"));
        Assert.Equal(1, await RestoreProgressCli.RunAsync(["restore-progress", path], missing,
            TextWriter.Null, TextWriter.Null, CancellationToken.None));
        Assert.False(File.Exists(missing.FilePath));
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("?format=xml", HttpStatusCode.BadRequest)]
    [InlineData("?format=JSON", HttpStatusCode.BadRequest)]
    [InlineData("?format=json&format=csv", HttpStatusCode.BadRequest)]
    public async Task ExportRequiresExactlyOneSupportedFormat(string query, HttpStatusCode status)
    {
        using var fixture = new DatabaseFixture();
        using var factory = new ExportFactory(fixture);
        using var client = await LoginClient(factory, "learner");
        using var response = await client.GetAsync("/api/learn/export" + query, CancellationToken.None);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("bad_format", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task CsvContainsBothTablesEscapesTextAndCannotLeakOtherUsers()
    {
        using var fixture = new DatabaseFixture();
        using var factory = new ExportFactory(fixture);
        using var client = await LoginClient(factory, "learner");
        using var response = await client.GetAsync("/api/learn/export?format=csv", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.StartsWith("table,id,userId,senseId,direction", csv);
        Assert.Contains("\r\ncards,", csv);
        Assert.Contains("\r\nreview_log,", csv);
        Assert.Contains("\"'=SUM(1,2),\"\"hola\"\"\nline\"", csv);
        Assert.DoesNotContain("reviewer-only-secret", csv);
    }

    [Fact]
    public async Task ExportRejectsUnauthenticatedAndReviewer()
    {
        using var fixture = new DatabaseFixture();
        using var factory = new ExportFactory(fixture);
        using var anonymous = factory.Client();
        using var unauthorized = await anonymous.GetAsync("/api/learn/export?format=json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var reviewer = await LoginClient(factory, "reviewer");
        using var forbidden = await reviewer.GetAsync("/api/learn/export?format=json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static async Task<string> AllRows(DatabaseSettings settings)
    {
        await using var database = settings.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Users = await database.Users.AsNoTracking().OrderBy(user => user.Id).ToArrayAsync(),
            Cards = await database.Cards.AsNoTracking().OrderBy(card => card.Id).ToArrayAsync(),
            Reviews = await database.ReviewLogs.AsNoTracking().OrderBy(review => review.Id).ToArrayAsync()
        });
    }

    private static async Task<int> RunProcess(DatabaseSettings settings, string file)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("restore-progress");
        start.ArgumentList.Add(file);
        start.Environment["ConnectionStrings__Default"] = $"Data Source={settings.FilePath}";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(cancellation.Token);
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private static async Task<HttpClient> LoginClient(ExportFactory factory, string username)
    {
        var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("X-Palabras", "1");
        request.Content = JsonContent.Create(new { username, password = "test-password" });
        using var login = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    private sealed class DatabaseFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Palabras.M10.Tests", Guid.NewGuid().ToString("N"));
        internal DatabaseSettings Settings { get; }
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash("test-password");

        internal DatabaseFixture(bool seed = true)
        {
            Directory.CreateDirectory(Root);
            Settings = new DatabaseSettings(Path.Combine(Root, "palabras.db"));
            if (!seed) return;
            using var database = Settings.CreateContext();
            database.Database.Migrate();
            database.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            database.Users.AddRange(new User { Id = 1, Username = "learner", Role = "owner", PasswordHash = PasswordHash, CreatedAt = Now, UpdatedAt = Now },
                new User { Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = PasswordHash, CreatedAt = Now, UpdatedAt = Now });
            database.Words.Add(new Word { Id = 1, Key = "es-casa-01", Headword = "casa", Status = "approved", ContentHash = "stable-content-hash" });
            database.Senses.Add(new Sense { Id = 1, WordId = 1, SenseOrder = 1, EnGloss = "house" });
            database.SaveChanges();
            database.Cards.AddRange(Card(1, 1), Card(2, 2));
            database.SaveChanges();
            database.ReviewLogs.AddRange(Review(1, 1), Review(2, 2));
            database.SaveChanges();
        }

        internal static Card Card(int id, int userId) => new()
        {
            Id = id, UserId = userId, SenseId = 1, State = CardState.Review, Reps = 3,
            Stability = 3.5, Difficulty = 4.2, DueMs = Now.AddDays(3).ToUnixTimeMilliseconds(),
            LastReviewMs = Now.ToUnixTimeMilliseconds(), CreatedAt = Now.AddDays(-5).ToUnixTimeMilliseconds()
        };

        internal static ReviewLog Review(int id, int userId) => new()
        {
            Id = id, UserId = userId, CardId = id, WordId = 1, Rating = Rating.Good,
            ClientAttemptId = $"00000000-0000-4000-8000-{id:D12}", ReviewedAtMs = Now.ToUnixTimeMilliseconds(),
            ReceivedAtMs = Now.ToUnixTimeMilliseconds(), IntervalAfterDays = 3, StateAfter = CardState.Review,
            AnswerText = userId == 2 ? "reviewer-only-secret" : "=SUM(1,2),\"hola\"\nline"
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ExportFactory(DatabaseFixture fixture) : WebApplicationFactory<Program>
    {
        internal HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://palabras.siebentower.nl"), AllowAutoRedirect = false, HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={fixture.Settings.FilePath}");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(fixture.Root, "keys"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            });
        }
    }

    private sealed class ObservedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private int _timerCount;
        internal FakeTimeProvider Fake { get; } = new(start);
        internal TaskCompletionSource<TimeSpan> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<TimeSpan> SecondTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            if (Interlocked.Increment(ref _timerCount) == 1) TimerCreated.TrySetResult(dueTime);
            else SecondTimerCreated.TrySetResult(dueTime);
            return timer;
        }
    }

    private sealed class CompletionLogger : ILogger<BackupService>
    {
        internal TaskCompletionSource<string> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Failed.TrySetResult();
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                foreach (var property in properties)
                    if (property.Key == "BackupFile") Published.TrySetResult((string)property.Value!);
        }
    }
}
