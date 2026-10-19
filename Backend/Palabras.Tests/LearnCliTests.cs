using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Palabras.Content;
using Palabras.Learn;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

public sealed class LearnCliTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T09:00:00Z", CultureInfo.InvariantCulture);
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "palabras-learn-cli-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public LearnCliTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new DatabaseSettings(Path.Combine(_directory, "palabras.db"));
    }

    [Fact]
    public async Task SettingsCreatesTheRowThenUpdatesItAndPrintsOldToNew()
    {
        await AddUserAsync("owner", "owner");
        var create = await Run(["learn", "settings", "--username", "owner", "--new-per-day", "200"]);
        Assert.Equal(0, create.Exit);
        Assert.Contains("new_per_day=12 -> 200", create.Output);
        Assert.Contains("max_reviews_per_day=200 -> 200", create.Output);
        Assert.Contains("backlog_threshold=150 -> 150", create.Output);
        await using (var database = _settings.CreateContext())
        {
            var row = await database.UserSettings.SingleAsync();
            Assert.Equal(200, row.NewPerDay);
            Assert.Equal(200, row.MaxReviewsPerDay);
            Assert.Equal(150, row.BacklogThreshold);
        }

        var update = await Run(["learn", "settings", "--username", "owner", "--new-per-day", "20",
            "--max-reviews-per-day", "50", "--backlog-threshold", "400"]);
        Assert.Equal(0, update.Exit);
        Assert.Contains("new_per_day=200 -> 20", update.Output);
        Assert.Contains("max_reviews_per_day=200 -> 50", update.Output);
        Assert.Contains("backlog_threshold=150 -> 400", update.Output);

        // No flag is a read: it prints the effective values and must not rewrite or create the row.
        var read = await Run(["learn", "settings", "--username", "owner"]);
        Assert.Equal(0, read.Exit);
        Assert.Contains("new_per_day=20", read.Output);
        Assert.DoesNotContain("->", read.Output);
        await using (var database = _settings.CreateContext())
        {
            Assert.Equal(1, await database.UserSettings.CountAsync());
            Assert.Equal(400, (await database.UserSettings.SingleAsync()).BacklogThreshold);
        }
    }

    [Theory]
    [InlineData("learn")]
    [InlineData("learn", "unknown")]
    [InlineData("learn", "settings")]
    [InlineData("learn", "settings", "--username")]
    [InlineData("learn", "settings", "--username", "owner", "--new-per-day", "abc")]
    [InlineData("learn", "settings", "--username", "owner", "--new-per-day", "-1")]
    [InlineData("learn", "settings", "--username", "owner", "--new-per-day", "1001")]
    [InlineData("learn", "settings", "--username", "owner", "--new-per-day", "12", "--new-per-day", "13")]
    [InlineData("learn", "settings", "--username", "owner", "--max-reviews-per-day", "10001")]
    [InlineData("learn", "settings", "--username", "owner", "--backlog-threshold", "100001")]
    [InlineData("learn", "seed-known", "--username", "owner")]
    [InlineData("learn", "seed-known", "--username", "owner", "--top", "0")]
    [InlineData("learn", "seed-known", "--username", "owner", "--top", "5", "--due-days", "0")]
    [InlineData("learn", "seed-known", "--username", "owner", "--top", "5", "--unknown")]
    public async Task InvalidArgumentsFailBeforeDatabaseAccess(params string[] args)
    {
        var result = await Run(args);
        Assert.Equal(1, result.Exit);
        Assert.StartsWith("Error: ", result.Error);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task UnknownUserFailsWithoutWritingSettings()
    {
        await AddUserAsync("owner", "owner");
        var result = await Run(["learn", "settings", "--username", "ghost", "--new-per-day", "200"]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Account not found: ghost", result.Error);
        await using var database = _settings.CreateContext();
        Assert.Empty(await database.UserSettings.ToListAsync());
    }

    [Fact]
    public async Task SeedKnownMovesTheRankedPrefixToReviewThenResetRestoresNew()
    {
        await AddUserAsync("owner", "owner");
        await ImportApprovedAsync(StandardEntries());
        var seed = await Run(["learn", "seed-known", "--username", "owner", "--top", "2"]);
        Assert.Equal(0, seed.Exit);
        Assert.Contains("seeded_words=2 seeded_cards=4", seed.Output);
        await using (var database = _settings.CreateContext())
        {
            // Rank order is (cefr, difficulty, id): beta 10, gamma 20, alfa 30, epsilon NULL, delta A2.
            Assert.Equal(new[] { "es-beta-01", "es-gamma-01" }, await KeysInStateAsync(database, CardState.Review));
            var seeded = await CardsForAsync(database, "owner", "es-beta-01", "es-gamma-01");
            Assert.Equal(4, seeded.Count);
            Assert.All(seeded, card =>
            {
                Assert.Equal(CardState.Review, card.State);
                Assert.Equal(30.0, card.Stability);
                Assert.Equal(1, card.Reps);
                Assert.NotNull(card.LastReviewMs);
                Assert.Null(card.IntroducedLocalDate);
                // The CLI runs on the system clock, so the due instant is 30 days from real now.
                Assert.InRange(card.DueMs, DateTimeOffset.UtcNow.AddDays(29).ToUnixTimeMilliseconds(),
                    DateTimeOffset.UtcNow.AddDays(31).ToUnixTimeMilliseconds());
            });
            var untouched = await CardsForAsync(database, "owner", "es-alfa-01", "es-delta-01", "es-epsilon-01");
            Assert.Equal(6, untouched.Count);
            Assert.All(untouched, card => Assert.Equal(CardState.New, card.State));
        }

        // Dry-run prints the ranked prefix and changes no timestamp.
        var before = await DueMsAsync();
        var dryRun = await Run(["learn", "seed-known", "--username", "owner", "--top", "2", "--dry-run"]);
        Assert.Equal(0, dryRun.Exit);
        Assert.Equal(new[] { "es-beta-01", "es-gamma-01", "dry-run=2" }, Lines(dryRun.Output));
        Assert.Equal(before, await DueMsAsync());

        // Re-running is idempotent: same words, same card count, no duplicate rows.
        var again = await Run(["learn", "seed-known", "--username", "owner", "--top", "2"]);
        Assert.Equal(0, again.Exit);
        Assert.Contains("seeded_words=2 seeded_cards=4", again.Output);

        var reset = await Run(["learn", "seed-known", "--username", "owner", "--top", "2", "--reset"]);
        Assert.Equal(0, reset.Exit);
        Assert.Contains("reset_words=2 reset_cards=4", reset.Output);
        await using (var database = _settings.CreateContext())
        {
            var restored = await CardsForAsync(database, "owner", "es-beta-01", "es-gamma-01");
            Assert.All(restored, card =>
            {
                Assert.Equal(CardState.New, card.State);
                Assert.Equal(0, card.Reps);
                Assert.Null(card.Stability);
                Assert.Null(card.LastReviewMs);
                Assert.Null(card.IntroducedLocalDate);
            });
            Assert.Equal(new[] { "es-alfa-01", "es-beta-01", "es-delta-01", "es-epsilon-01", "es-gamma-01" },
                await KeysInStateAsync(database, CardState.New));
        }
    }

    [Fact]
    public async Task SeedKnownOnlySelectsApprovedWordsAndSeedsEveryOneWhenFewerThanTop()
    {
        await AddUserAsync("owner", "owner");
        var corpus = await SyntheticCorpus.WriteAsync(Path.Combine(_directory, "corpus"),
            new SyntheticCorpus.Entry("es-alfa-01", "alfa", "A1", 10),
            new SyntheticCorpus.Entry("es-beta-01", "beta", "A1", 20),
            new SyntheticCorpus.Entry("es-gamma-01", "gamma", "A1", 5));
        var imported = await new ContentImporter(_settings).ImportAsync(corpus, false, CancellationToken.None);
        Assert.Equal(3, imported.Created);
        Assert.Empty(imported.Rejected);
        // Import now approves by default; this test needs one unapproved word, so gamma is put back
        // into review and the import's automatic card pass is removed before seeding.
        await using (var importDatabase = _settings.CreateContext())
        {
            await importDatabase.Cards.ExecuteDeleteAsync();
            await importDatabase.Words.Where(word => word.Key == "es-gamma-01")
                .ExecuteUpdateAsync(setters => setters.SetProperty(word => word.Status, "pending_review"));
        }
        // gamma ranks first but stays pending: seeding may never touch unapproved content.
        var approved = await RunContent(["content", "approve", "es-alfa-01", "es-beta-01"]);
        Assert.Equal(0, approved.Exit);

        var seed = await Run(["learn", "seed-known", "--username", "owner", "--top", "10"]);
        Assert.Equal(0, seed.Exit);
        Assert.Contains("seeded_words=2 seeded_cards=4", seed.Output);
        await using var database = _settings.CreateContext();
        Assert.Equal(0, await database.Cards.CountAsync(card => database.Senses.Any(sense => sense.Id == card.SenseId
            && database.Words.Any(word => word.Id == sense.WordId && word.Key == "es-gamma-01"))));
    }

    [Fact]
    public async Task RaisedSettingsOfferTheBudgetBoundFirstTwentyThenTheNextTwenty()
    {
        await AddUserAsync("owner", "owner");
        // Ranks are reversed against file/id order, so the offer can only be right if the
        // difficulty ordering (not the import order) drives it.
        var entries = Enumerable.Range(1, 60)
            .Select(index => new SyntheticCorpus.Entry($"es-word{index:D3}-01", $"word{index:D3}", "A1", (61 - index) * 10))
            .ToArray();
        await ImportApprovedAsync(entries);
        var expected = entries.OrderBy(entry => entry.Difficulty!.Value).Select(entry => entry.Key).ToArray();

        var settings = await Run(["learn", "settings", "--username", "owner", "--new-per-day", "200"]);
        Assert.Equal(0, settings.Exit);
        Assert.Contains("new_per_day=12 -> 200", settings.Output);

        var timeProvider = new FakeTimeProvider(Now);
        var clock = new LearnerClock(timeProvider, "Europe/Madrid");
        var bounds = QueueBounds.From(clock, 1);
        await using var database = _settings.CreateContext();
        var active = await ActiveSenses.LoadAsync(database, null, CancellationToken.None);
        var stored = await database.UserSettings.AsNoTracking().SingleAsync(row => row.UserId == 1);
        var first = await PlanComposer.ComposeAsync(database, bounds, active, clock, stored, null, null,
            CancellationToken.None);
        // 200/day is not 200 per sitting: the 600 s budget still caps a sitting at 20 new words.
        Assert.Equal(20, first.NewOffered);
        Assert.Equal(expected.Take(20), first.Items.Select(item => item.Word.Key).ToArray());

        // Finish the sitting the way LearnAttempts.Apply does: the cards leave New (here: graduated
        // and due tomorrow, so they are not due again today) and the introduction date is stamped.
        var firstCardIds = first.Items.Select(item => item.CardId).ToArray();
        var today = clock.LocalToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tomorrowMs = clock.EndOfLocalDayUtc.AddDays(1).ToUnixTimeMilliseconds();
        await database.Cards.Where(card => firstCardIds.Contains(card.Id)).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(card => card.IntroducedLocalDate, today)
                .SetProperty(card => card.State, CardState.Review)
                .SetProperty(card => card.DueMs, tomorrowMs)
                .SetProperty(card => card.Reps, 1), CancellationToken.None);
        var second = await PlanComposer.ComposeAsync(database, bounds, active, clock, stored, null, null,
            CancellationToken.None);
        Assert.Equal(20, second.NewOffered);
        Assert.Equal(expected.Skip(20).Take(20), second.Items.Select(item => item.Word.Key).ToArray());
    }

    private static SyntheticCorpus.Entry[] StandardEntries() =>
    [
        new("es-alfa-01", "alfa", "A1", 30),
        new("es-beta-01", "beta", "A1", 10),
        new("es-gamma-01", "gamma", "A1", 20),
        new("es-delta-01", "delta", "A2", 5),
        new("es-epsilon-01", "epsilon", "A1", null)
    ];

    private async Task AddUserAsync(string username, string role)
    {
        await DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None);
        await using var database = _settings.CreateContext();
        database.Users.Add(new User
        {
            Username = username, Role = role, PasswordHash = "hash", CreatedAt = Now, UpdatedAt = Now
        });
        await database.SaveChangesAsync(CancellationToken.None);
    }

    // Approval goes through the shipped CLI so the cards come from ContentCardLifecycle, exactly
    // as they will in the bring-up sequence.
    private async Task ImportApprovedAsync(params SyntheticCorpus.Entry[] entries)
    {
        var corpus = await SyntheticCorpus.WriteAsync(Path.Combine(_directory, "corpus"), entries);
        var imported = await new ContentImporter(_settings).ImportAsync(corpus, false, CancellationToken.None);
        Assert.Empty(imported.Rejected);
        Assert.Equal(entries.Length, imported.Created);
        // Trust-by-default imports land approved and the importer already created the cards; the
        // CLI pass remains a safe idempotent backfill for an upgraded corpus.
        var approved = await RunContent(["content", "approve", "--all-pending"]);
        Assert.Equal(0, approved.Exit);
        Assert.Contains("approved=0 unchanged=0", approved.Output);
    }

    private static async Task<string[]> KeysInStateAsync(PalabrasDbContext database, CardState state)
    {
        var userId = await database.Users.AsNoTracking().Where(user => user.Username == "owner")
            .Select(user => user.Id).SingleAsync();
        return await (from card in database.Cards.AsNoTracking()
                      join sense in database.Senses on card.SenseId equals sense.Id
                      join word in database.Words on sense.WordId equals word.Id
                      where card.UserId == userId && card.State == state
                      select word.Key).Distinct().OrderBy(key => key).ToArrayAsync();
    }

    private static async Task<List<Card>> CardsForAsync(PalabrasDbContext database, string username, params string[] keys)
    {
        var userId = await database.Users.AsNoTracking().Where(user => user.Username == username)
            .Select(user => user.Id).SingleAsync();
        var wordIds = await database.Words.AsNoTracking().Where(word => keys.Contains(word.Key))
            .Select(word => word.Id).ToArrayAsync();
        return await database.Cards.AsNoTracking().Where(card => card.UserId == userId
                && database.Senses.Any(sense => sense.Id == card.SenseId && wordIds.Contains(sense.WordId)))
            .ToListAsync();
    }

    private async Task<long[]> DueMsAsync()
    {
        await using var database = _settings.CreateContext();
        return await database.Cards.AsNoTracking().OrderBy(card => card.Id).Select(card => card.DueMs).ToArrayAsync();
    }

    private static string[] Lines(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<(int Exit, string Output, string Error)> Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LearnCli.RunAsync(args, _settings, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> RunContent(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ContentCli.RunAsync(args, _settings, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
