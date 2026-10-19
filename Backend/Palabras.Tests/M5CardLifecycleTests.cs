using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Palabras.Content;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

public sealed class M5CardLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "palabras-card-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));

    public M5CardLifecycleTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "content"));
        _settings = new DatabaseSettings(Path.Combine(_directory, "data", "palabras.db"));
        File.Copy(Path.Combine(M2ContentImportTests.CorpusDirectory(), "es-coger-01.json"), SourcePath);
    }

    [Fact]
    public async Task ApprovalCreatesTwoNewCardsPerSenseForLearnerOnlyAndRepeatPreservesEveryColumn()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        Assert.Equal(new ContentApprovalResult(1, 0), await Approve(database));
        var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
        Assert.Equal(4, cards.Count);
        Assert.All(cards.GroupBy(card => card.SenseId), group => Assert.Equal(new[] { "en_es", "es_en" }, group.Select(card => card.Direction).Order().ToArray()));
        Assert.All(cards, card =>
        {
            Assert.Equal(1, card.UserId);
            Assert.Equal(CardState.New, card.State);
            Assert.Equal(0, card.Epoch);
            Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), card.CreatedAt);
            Assert.Equal(card.CreatedAt, card.DueMs);
            Assert.Null(card.LastReviewMs);
            Assert.Null(card.Stability);
            Assert.Null(card.Difficulty);
            Assert.Null(card.Step);
            Assert.Null(card.SuspendedReason);
            Assert.Null(card.IntroducedLocalDate);
            Assert.Equal(0, card.Reps);
            Assert.Equal(0, card.Lapses);
        });
        cards[0].State = CardState.Review;
        cards[0].Reps = 12;
        cards[0].Lapses = 2;
        cards[0].Stability = 42;
        cards[0].Difficulty = 6;
        cards[0].LastReviewMs = 100;
        cards[0].DueMs = 999;
        cards[0].IntroducedLocalDate = "2026-10-17";
        cards[0].BuriedUntilLocalDate = "2026-10-19";
        await database.SaveChangesAsync();
        var before = JsonSerializer.Serialize(cards);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new ContentApprovalResult(0, 1), await Approve(database));
        Assert.Equal(before, JsonSerializer.Serialize(await database.Cards.AsNoTracking().OrderBy(card => card.Id).ToListAsync()));
        Assert.Equal(1, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyApprovedWordBackfillsOnlyMissingCardsWithoutNewRevisions(bool allPending)
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await database.Words.ExecuteUpdateAsync(setters => setters.SetProperty(word => word.Status, "approved"));
        Assert.Equal(new ContentApprovalResult(0, allPending ? 0 : 1), await Approve(database, allPending));
        Assert.Equal(4, await database.Cards.CountAsync());
        var retained = await database.Cards.OrderBy(card => card.Id).FirstAsync();
        retained.SuspendedReason = "manual";
        retained.Epoch = 7;
        retained.Reps = 19;
        await database.SaveChangesAsync();
        var before = JsonSerializer.Serialize(retained);
        await database.Cards.Where(card => card.Id != retained.Id).ExecuteDeleteAsync();
        database.ChangeTracker.Clear();
        await Approve(database, allPending);
        Assert.Equal(4, await database.Cards.CountAsync());
        Assert.Equal(before, JsonSerializer.Serialize(await database.Cards.AsNoTracking().SingleAsync(card => card.Id == retained.Id)));
        Assert.Equal(0, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("leech")]
    public async Task ChangedSourceKeepsAnApprovedWordEligibleAndPreservesProtectedPauses(string protectedReason)
    {
        await ImportAndSeed();
        await using (var database = _settings.CreateContext())
        {
            await Approve(database);
            var card = await database.Cards.OrderBy(card => card.Id).FirstAsync();
            card.SuspendedReason = protectedReason;
            card.IsLeech = protectedReason == "leech";
            card.State = CardState.Review;
            card.Reps = 15;
            card.Lapses = 8;
            card.Stability = 20;
            await database.SaveChangesAsync();
        }
        await EditAndImport();
        await using (var database = _settings.CreateContext())
        {
            // Trust-by-default: the changed source re-approves at the new version; the word is not
            // dropped from the learner's queue and only the manual/leech pause survives.
            Assert.Equal("approved", (await database.Words.SingleAsync()).Status);
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.Equal(protectedReason, cards[0].SuspendedReason);
            Assert.All(cards.Skip(1), card => Assert.Null(card.SuspendedReason));
            Assert.All(cards, card => Assert.Equal(1, card.Epoch));
            await Approve(database);
            Assert.Equal(protectedReason, cards[0].SuspendedReason);
            Assert.All(cards.Skip(1), card => Assert.Null(card.SuspendedReason));
            Assert.All(cards, card => Assert.Equal(1, card.Epoch));
            Assert.Equal(15, cards[0].Reps);
            Assert.Equal(8, cards[0].Lapses);
            Assert.Equal(20, cards[0].Stability);
            Assert.Equal(CardState.Review, cards[0].State);
            await Approve(database);
            Assert.All(cards, card => Assert.Equal(1, card.Epoch));
        }
    }

    [Fact]
    public async Task RetirementIsAtomicRevisionIdempotentAndReapprovalRestoresOnlyApprovalSuspension()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        var card = await database.Cards.OrderBy(value => value.Id).FirstAsync();
        card.SuspendedReason = "manual";
        await database.SaveChangesAsync();
        var unknown = await Run("content", "retire", "es-coger-01", "es-unknown-01");
        Assert.Equal(1, unknown.Exit);
        Assert.Equal("approved", (await database.Words.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, (await database.Cards.AsNoTracking().FirstAsync()).Epoch);
        Assert.Equal((0, "retired=1" + Environment.NewLine), await Run("content", "retire", "es-coger-01"));
        Assert.Equal((0, "retired=0" + Environment.NewLine), await Run("content", "retire", "es-coger-01"));
        database.ChangeTracker.Clear();
        var cards = await database.Cards.OrderBy(value => value.Id).ToListAsync();
        Assert.Equal("manual", cards[0].SuspendedReason);
        Assert.All(cards.Skip(1), value => Assert.Equal("word_not_approved", value.SuspendedReason));
        Assert.All(cards, value => Assert.Equal(1, value.Epoch));
        var revision = await database.WordRevisions.SingleAsync(value => value.Action == "retire");
        Assert.Null(revision.ActorUserId);
        Assert.Equal("retired", JsonNode.Parse(revision.SnapshotJson)!["status"]!.GetValue<string>());
        await Approve(database);
        Assert.Equal("manual", cards[0].SuspendedReason);
        Assert.All(cards.Skip(1), value => Assert.Null(value.SuspendedReason));
        Assert.All(cards, value => Assert.Equal(2, value.Epoch));
    }

    [Fact]
    public async Task CreatedCardsObeySqliteNewDirectionDueStateAndDemotionPredicates()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        var eligible = from card in database.Cards
                       join sense in database.Senses on card.SenseId equals sense.Id
                       join word in database.Words on sense.WordId equals word.Id
                       where card.UserId == 1 && card.SuspendedReason == null && word.Status == "approved"
                           && card.DueMs <= _time.GetUtcNow().ToUnixTimeMilliseconds()
                       select card;
        var fresh = eligible.Where(card => card.State == CardState.New && card.Direction == "es_en");
        var due = eligible.Where(card => card.State != CardState.New && card.Direction == "es_en");
        Assert.Contains("\"state\" <> 0", due.ToQueryString());
        Assert.Contains("\"direction\" = 'es_en'", fresh.ToQueryString());
        Assert.Equal(2, await fresh.CountAsync());
        Assert.Equal(0, await due.CountAsync());
        var introduced = await fresh.FirstAsync();
        introduced.State = CardState.Learning;
        await database.SaveChangesAsync();
        Assert.Equal(1, await fresh.CountAsync());
        Assert.Equal(1, await due.CountAsync());
        await EditAndImport();
        // The word stays approved, so the untouched new card and the learning card both remain
        // eligible (epoch bumped, not suspended).
        Assert.Equal(1, await fresh.CountAsync());
        Assert.Equal(1, await due.CountAsync());
    }

    [Fact]
    public async Task CardInsertFailureRollsBackApprovalStatusAndRevision()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_cards BEFORE INSERT ON cards BEGIN SELECT RAISE(ABORT, 'forced card failure'); END;");
        Assert.Equal(1, (await Run("content", "approve", "es-coger-01")).Exit);
        Assert.Equal("pending_review", (await database.Words.SingleAsync()).Status);
        Assert.Equal(0, await database.Cards.CountAsync());
        Assert.Equal(0, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
    }

    [Fact]
    public async Task UnchangedImportDoesNotDemoteOrInvalidateExistingCards()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        var result = await new ContentImporter(_settings).ImportAsync(Path.GetDirectoryName(SourcePath)!, false, CancellationToken.None);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal("approved", (await database.Words.AsNoTracking().SingleAsync()).Status);
        Assert.All(await database.Cards.AsNoTracking().ToListAsync(), card => { Assert.Equal(0, card.Epoch); Assert.Null(card.SuspendedReason); });
    }

    [Fact]
    public async Task ApprovedBackfillClearsOnlyApprovalReasonAndInvalidatesOldEpoch()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
        cards[0].SuspendedReason = "word_not_approved";
        cards[1].SuspendedReason = "leech";
        await database.SaveChangesAsync();
        await Approve(database, true);
        Assert.Null(cards[0].SuspendedReason);
        Assert.Equal(1, cards[0].Epoch);
        Assert.Equal("leech", cards[1].SuspendedReason);
        Assert.Equal(0, cards[1].Epoch);
        Assert.All(cards.Skip(2), card => Assert.Equal(0, card.Epoch));
    }

    [Fact]
    public async Task RetirementCardFailureRollsBackStatusAndRevision()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_retirement BEFORE UPDATE ON cards BEGIN SELECT RAISE(ABORT, 'forced retirement failure'); END;");
        Assert.Equal(1, (await Run("content", "retire", "es-coger-01")).Exit);
        Assert.Equal("approved", (await database.Words.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await database.WordRevisions.CountAsync(revision => revision.Action == "retire"));
        Assert.All(await database.Cards.AsNoTracking().ToListAsync(), card => { Assert.Equal(0, card.Epoch); Assert.Null(card.SuspendedReason); });
    }

    [Fact]
    public async Task RemovingUncardedSenseStillDeletesIt()
    {
        await ImportAndSeed();
        await WriteSenseCountAndImport(1);
        await using var database = _settings.CreateContext();
        Assert.Equal(1, await database.Senses.CountAsync());
        await Approve(database);
        Assert.Equal(2, await database.Cards.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("manual")]
    [InlineData("leech")]
    public async Task RemovedReviewedSenseRetainsHistoryAndRestoresOnlyWhenSourceOrdinalReturns(string? protectedReason)
    {
        await ImportAndSeed();
        int removedSenseId;
        string history;
        int[] cardIds;
        await using (var database = _settings.CreateContext())
        {
            await Approve(database);
            var sense = await database.Senses.SingleAsync(value => value.SenseOrder == 1);
            removedSenseId = sense.Id;
            var cards = await database.Cards.Where(card => card.SenseId == sense.Id).OrderBy(card => card.Id).ToListAsync();
            cardIds = cards.Select(card => card.Id).ToArray();
            cards[0].SuspendedReason = protectedReason;
            cards[0].IsLeech = protectedReason == "leech";
            foreach (var card in cards)
            {
                card.State = CardState.Review;
                card.Stability = 42;
                card.Reps = 5;
                database.ReviewLogs.Add(new ReviewLog
                {
                    UserId = card.UserId, WordId = sense.WordId, CardId = card.Id, CardEpoch = card.Epoch,
                    ClientAttemptId = "removed-history-" + card.Id, Rating = Rating.Good,
                    ReviewedAtMs = 100, ReceivedAtMs = 100, CountsForSrs = true
                });
            }
            await database.SaveChangesAsync();
            history = JsonSerializer.Serialize(await database.ReviewLogs.AsNoTracking().OrderBy(log => log.Id).ToListAsync());
        }
        await WriteSenseCountAndImport(1);
        await using (var database = _settings.CreateContext())
        {
            Assert.Equal(2, await database.Senses.CountAsync());
            Assert.Equal(4, await database.Cards.CountAsync());
            Assert.Equal("approved", (await database.Words.SingleAsync()).Status);
            var removed = await database.Cards.Where(card => card.SenseId == removedSenseId).OrderBy(card => card.Id).ToListAsync();
            // The retained removed sense is marked sense_removed; the live sense stays eligible.
            Assert.Equal(protectedReason ?? "sense_removed", removed[0].SuspendedReason);
            Assert.Equal("sense_removed", removed[1].SuspendedReason);
            // Only live senses are invalidated by the changed-content epoch bump.
            Assert.All(removed, card => Assert.Equal(0, card.Epoch));
            Assert.All(await database.Cards.Where(card => card.SenseId != removedSenseId).ToListAsync(),
                card => Assert.Null(card.SuspendedReason));
            await Approve(database);
            await Approve(database, true);
            Assert.Equal(protectedReason ?? "sense_removed", removed[0].SuspendedReason);
            Assert.Equal("sense_removed", removed[1].SuspendedReason);
            Assert.All(removed, card => Assert.Equal(0, card.Epoch));
            Assert.Equal(2, await database.Cards.CountAsync(card => card.SuspendedReason == null));
        }
        await WriteSenseCountAndImport(2);
        await using (var database = _settings.CreateContext())
        {
            var removed = await database.Cards.Where(card => card.SenseId == removedSenseId).OrderBy(card => card.Id).ToListAsync();
            Assert.Equal(cardIds, removed.Select(card => card.Id).ToArray());
            // The importer's approved-content card pass already releases the word_not_approved
            // marker; protected manual/leech pauses survive it.
            Assert.Equal(protectedReason, removed[0].SuspendedReason);
            Assert.Null(removed[1].SuspendedReason);
            Assert.All(removed, card => Assert.Equal(1, card.Epoch));
            await Approve(database);
            Assert.Equal(protectedReason, removed[0].SuspendedReason);
            Assert.Null(removed[1].SuspendedReason);
            Assert.All(removed, card =>
            {
                Assert.Equal(1, card.Epoch);
                Assert.Equal(CardState.Review, card.State);
                Assert.Equal(42, card.Stability);
                Assert.Equal(5, card.Reps);
            });
            Assert.Equal(history, JsonSerializer.Serialize(await database.ReviewLogs.AsNoTracking().OrderBy(log => log.Id).ToListAsync()));
            Assert.Equal(4, await database.Cards.CountAsync());
        }
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("leech")]
    public async Task ApprovalBackfillNeverCreatesMissingDirectionForRemovedProtectedSense(string protectedReason)
    {
        await ImportAndSeed();
        int removedSenseId;
        await using (var database = _settings.CreateContext())
        {
            await Approve(database);
            removedSenseId = (await database.Senses.SingleAsync(sense => sense.SenseOrder == 1)).Id;
            await database.Cards.Where(card => card.SenseId == removedSenseId && card.Direction == "en_es").ExecuteDeleteAsync();
            var card = await database.Cards.SingleAsync(card => card.SenseId == removedSenseId);
            card.SuspendedReason = protectedReason;
            await database.SaveChangesAsync();
        }
        await WriteSenseCountAndImport(1);
        await using (var database = _settings.CreateContext())
        {
            await Approve(database);
            await Approve(database, true);
            Assert.Equal(3, await database.Cards.CountAsync());
            Assert.Equal(protectedReason, (await database.Cards.SingleAsync(card => card.SenseId == removedSenseId)).SuspendedReason);
        }
        await WriteSenseCountAndImport(2);
        await using (var database = _settings.CreateContext())
        {
            await Approve(database);
            Assert.Equal(4, await database.Cards.CountAsync());
            Assert.Equal(protectedReason, (await database.Cards.SingleAsync(card => card.SenseId == removedSenseId && card.Direction == "es_en")).SuspendedReason);
            Assert.Null((await database.Cards.SingleAsync(card => card.SenseId == removedSenseId && card.Direction == "en_es")).SuspendedReason);
        }
    }

    [Fact]
    public async Task DemotionFailureRollsBackContentAndEpoch()
    {
        await ImportAndSeed();
        await using var database = _settings.CreateContext();
        await Approve(database);
        var hash = (await database.Words.SingleAsync()).ContentHash;
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_demotion BEFORE UPDATE ON cards BEGIN SELECT RAISE(ABORT, 'forced demotion failure'); END;");
        await EditSource();
        await Assert.ThrowsAsync<DbUpdateException>(() => new ContentImporter(_settings).ImportAsync(Path.GetDirectoryName(SourcePath)!, false, CancellationToken.None));
        Assert.Equal("approved", (await database.Words.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(hash, (await database.Words.AsNoTracking().SingleAsync()).ContentHash);
        Assert.All(await database.Cards.AsNoTracking().ToListAsync(), card => { Assert.Equal(0, card.Epoch); Assert.Null(card.SuspendedReason); });
    }

    private string SourcePath => Path.Combine(_directory, "content", "es-coger-01.json");

    private async Task ImportAndSeed()
    {
        var result = await new ContentImporter(_settings).ImportAsync(Path.GetDirectoryName(SourcePath)!, false, CancellationToken.None);
        Assert.Equal(1, result.Created);
        Assert.Empty(result.Rejected);
        await using var database = _settings.CreateContext();
        database.Users.AddRange(new User { Id = 1, Username = "owner", Role = "owner", PasswordHash = "hash" },
            new User { Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = "hash" });
        // These tests exercise the explicit approval lifecycle. Undo the trust-by-default import
        // (approved word plus its automatic card pass) so the CLI approval under test is the writer.
        await database.Cards.ExecuteDeleteAsync();
        await database.Words.ExecuteUpdateAsync(setters => setters.SetProperty(word => word.Status, "pending_review"));
        await database.SaveChangesAsync();
    }

    private Task<ContentApprovalResult> Approve(PalabrasDbContext database, bool allPending = false) =>
        ContentApproval.ApproveAsync(database, allPending ? [] : ["es-coger-01"], allPending, CancellationToken.None, _time);

    private async Task EditSource()
    {
        var source = JsonNode.Parse(await File.ReadAllTextAsync(SourcePath))!;
        source["notes"] = "Changed content requires fresh approval.";
        await File.WriteAllTextAsync(SourcePath, source.ToJsonString());
    }

    private async Task EditAndImport()
    {
        await EditSource();
        var result = await new ContentImporter(_settings).ImportAsync(Path.GetDirectoryName(SourcePath)!, false, CancellationToken.None);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Rejected);
    }

    private async Task WriteSenseCountAndImport(int count)
    {
        var source = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(M2ContentImportTests.CorpusDirectory(), "es-coger-01.json")))!;
        while (source["senses"]!.AsArray().Count > count) source["senses"]!.AsArray().RemoveAt(count);
        await File.WriteAllTextAsync(SourcePath, source.ToJsonString());
        var result = await new ContentImporter(_settings).ImportAsync(Path.GetDirectoryName(SourcePath)!, false, CancellationToken.None);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Rejected);
    }

    private async Task<(int Exit, string Output)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ContentCli.RunAsync(args, _settings, output, error, CancellationToken.None);
        return (exit, output.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
