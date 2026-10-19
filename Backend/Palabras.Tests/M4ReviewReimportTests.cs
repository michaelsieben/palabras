using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Xunit;

namespace Palabras.Tests;

public sealed partial class M4ReviewApiTests
{
    [Theory]
    [InlineData("approve_edit")]
    [InlineData("approve_alt")]
    public async Task UnchangedSourceKeepsReviewerContentVersionHashAndVerdict(string action)
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        AssertImport(await ImportSourceAsync(factory), 1, 0, 0);
        using var reviewer = await LoginAsync(factory, "reviewer");
        int wordId;
        string hash;
        int alternativeId;
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            wordId = word.Id;
            hash = word.ContentHash;
            alternativeId = await database.WordAlternatives.OrderBy(alt => alt.Id).Select(alt => alt.Id).FirstAsync();
        }
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { action == "approve_edit"
                ? EditAction(Guid.NewGuid().ToString(), wordId, "abuelita")
                : AltAction(Guid.NewGuid().ToString(), wordId, alternativeId, 1) }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        AssertImport(await ImportSourceAsync(factory), created: 0, updated: 0, unchanged: 1);
        AssertImport(await ImportSourceAsync(factory), created: 0, updated: 0, unchanged: 1);
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            Assert.Equal("abuelita", word.Headword);
            Assert.Equal(hash, word.ContentHash);
            Assert.Equal(2, word.Version);
            Assert.Equal(2, word.ApprovedVersion);
            Assert.Equal("approved", word.Status);
            Assert.Equal(3, await database.WordRevisions.CountAsync());
            Assert.Single(await database.WordReviews.ToListAsync());
            Assert.Equal(3, await database.WordAlternatives.CountAsync());
            Assert.Single(await database.Senses.ToListAsync());
            Assert.Single(await database.WordTopics.ToListAsync());
            // The import's trust-by-default card pass created the cards at epoch 0; the reviewer's
            // content edit is a content change, so it invalidates them exactly once.
            Assert.All(await database.Cards.ToListAsync(), card => Assert.Equal(1, card.Epoch));
        }
    }

    [Theory]
    [InlineData("approve_edit")]
    [InlineData("approve_alt")]
    public async Task ChangedSourceWithReviewerEditReturnsToReviewWithoutLosingHistoricalVerdicts(string action)
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        AssertImport(await ImportSourceAsync(factory), 1, 0, 0);
        using var reviewer = await LoginAsync(factory, "reviewer");
        int wordId;
        int alternativeId;
        string originalHash;
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            wordId = word.Id;
            originalHash = word.ContentHash;
            alternativeId = await database.WordAlternatives.OrderBy(alt => alt.Id).Select(alt => alt.Id).FirstAsync();
        }
        var clientReviewId = Guid.NewGuid().ToString();
        var originalAction = action == "approve_edit" ? EditAction(clientReviewId, wordId, "abuelita")
            : AltAction(clientReviewId, wordId, alternativeId, 1);
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { originalAction }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await WriteSourceAsync(factory, "grandmother from changed source");
        AssertImport(await ImportSourceAsync(factory), 0, 1, 0);
        AssertImport(await ImportSourceAsync(factory), 0, 0, 1);
        AssertImport(await ImportSourceAsync(factory), 0, 0, 1);
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            Assert.Equal("abuela", word.Headword);
            Assert.NotEqual(originalHash, word.ContentHash);
            Assert.Equal(3, word.Version);
            // Trust-by-default, except for a reviewer edit: the changed source may not silently
            // overwrite what she wrote, so the word goes back to the queue for re-review instead of
            // being re-approved at the new source version.
            Assert.Equal("pending_review", word.Status);
            Assert.NotNull(word.StatusChangedAt);
            Assert.Equal(2, word.ApprovedVersion); // Last version a human actually accepted.
            Assert.Equal("grandmother from changed source", (await database.Senses.SingleAsync()).EnGloss);
            Assert.Equal(2, await database.WordAlternatives.CountAsync());
            Assert.Single(await database.WordTopics.ToListAsync());
            var revisions = await database.WordRevisions.OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(new[] { 1, 2, 2, 3 }, revisions.Select(row => row.Version).ToArray());
            Assert.Equal("abuelita", JsonNode.Parse(revisions[2].SnapshotJson)!["headword"]!.GetValue<string>());
            Assert.Contains(revisions, row => row.Action == "edit" && row.Version == 2);
            var verdict = await database.WordReviews.SingleAsync();
            Assert.Equal(clientReviewId, verdict.ClientReviewId);
            Assert.Equal(1, verdict.WordVersion); // Version the reviewer saw, still preserved in revisions.
            Assert.Contains(revisions, row => row.Version == verdict.WordVersion);
            Assert.Contains(revisions, row => row.Version == word.ApprovedVersion);
            Assert.Contains("abuelita", verdict.EditDiff!);
            Assert.Null(verdict.ChosenAlternativeId); // ON DELETE SET NULL; diff/snapshots preserve chosen content.
            Assert.All(await database.Cards.ToListAsync(), card =>
            {
                // edit (+1) and the demoting changed-source import (+1); the word left the approved
                // queue, so the cards stay suspended until a reviewer re-approves it.
                Assert.Equal(2, card.Epoch);
                Assert.Equal("word_not_approved", card.SuspendedReason);
            });
        }
        // The opposite ordering: queued edit for v2 cannot approve newly imported v3.
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { new { clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 2,
                action = "approve_edit", edits = new[] { new { field = "lemma", after = "vieja" } } } }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("stale_version", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { originalAction }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("duplicate", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        // The default queue scope is pending: the returned word is listed there, at its new version.
        using (var queue = await reviewer.GetAsync("/api/review/queue"))
        {
            using var queueBody = await JsonAsync(queue);
            var item = Assert.Single(queueBody.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(3, item.GetProperty("version").GetInt32());
            Assert.Equal("pending_review", item.GetProperty("status").GetString());
            Assert.Equal(1, queueBody.RootElement.GetProperty("pendingTotal").GetInt32());
        }
        // ... and the approved scope no longer offers it, because it is back in review.
        using (var approved = await reviewer.GetAsync("/api/review/queue?status=approved"))
        {
            using var approvedBody = await JsonAsync(approved);
            Assert.Empty(approvedBody.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(0, approvedBody.RootElement.GetProperty("approvedTotal").GetInt32());
        }
    }

    [Fact]
    public async Task ChangedSourceWithoutReviewerEditStaysApproved()
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        AssertImport(await ImportSourceAsync(factory), 1, 0, 0);
        await WriteSourceAsync(factory, "grandmother from changed source");
        AssertImport(await ImportSourceAsync(factory), 0, 1, 0);
        AssertImport(await ImportSourceAsync(factory), 0, 0, 1);
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            // No reviewer edit exists, so trust-by-default applies: the changed source is accepted
            // and the word never leaves the learner's queue.
            Assert.Equal("approved", word.Status);
            Assert.Equal(2, word.Version);
            Assert.Equal(2, word.ApprovedVersion);
            Assert.Equal("grandmother from changed source", (await database.Senses.SingleAsync()).EnGloss);
            Assert.Empty(await database.WordReviews.ToListAsync());
            Assert.Empty(await database.WordRevisions.Where(row => row.Action == "edit").ToListAsync());
            Assert.Equal(new[] { 1, 2 },
                (await database.WordRevisions.OrderBy(row => row.Id).ToListAsync()).Select(row => row.Version).ToArray());
            Assert.All(await database.Cards.ToListAsync(), card =>
            {
                Assert.Equal(1, card.Epoch);
                Assert.Null(card.SuspendedReason);
            });
        }
        using var reviewer = await LoginAsync(factory, "reviewer");
        using (var queue = await reviewer.GetAsync("/api/review/queue"))
        {
            using var queueBody = await JsonAsync(queue);
            Assert.Empty(queueBody.RootElement.GetProperty("items").EnumerateArray());
        }
        using (var approved = await reviewer.GetAsync("/api/review/queue?status=approved"))
        {
            using var approvedBody = await JsonAsync(approved);
            Assert.Equal(2, Assert.Single(approvedBody.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("version").GetInt32());
        }
    }

    [Theory]
    [InlineData("lemma", 61)]
    [InlineData("lemma", 0)]
    [InlineData("gloss", 121)]
    [InlineData("gloss", 0)]
    [InlineData("example", 141)]
    [InlineData("example", 0)]
    [InlineData("regional_note", 301)]
    [InlineData("accepted", 121)]
    public async Task ReviewerEditsCannotBypassImportFieldSchema(string field, int length)
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        await ImportSourceAsync(factory);
        using var reviewer = await LoginAsync(factory, "reviewer");
        using var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1,
                action = "approve_edit", edits = new object[] {
                    new { field = "lemma", after = "abuelita" },
                    new { field, senseOrder = 0, after = new string('x', length) } } } }
        });
        using var body = await JsonAsync(response);
        Assert.Equal("invalid", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray()).GetProperty("result").GetString());
        await using var database = factory.OpenDatabase();
        Assert.Equal("abuela", (await database.Words.SingleAsync()).Headword);
        Assert.Equal(1, (await database.Words.SingleAsync()).Version);
        Assert.Empty(await database.WordReviews.ToListAsync());
        Assert.Single(await database.WordRevisions.ToListAsync());
    }

    [Fact]
    public async Task AlternativeCannotBypassHeadwordSchemaWhenItsOwnValueLimitIsLarger()
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        var path = Path.Combine(factory.ImportDirectory, "es-abuela-01.json");
        var source = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        source["alternatives"]![0]!["value"] = new string('x', 61);
        await File.WriteAllTextAsync(path, source.ToJsonString());
        AssertImport(await ImportSourceAsync(factory), 1, 0, 0); // Alternative schema permits 80 chars.
        int chipId;
        await using (var database = factory.OpenDatabase())
            chipId = await database.WordAlternatives.OrderBy(row => row.Id).Select(row => row.Id).FirstAsync();
        using var reviewer = await LoginAsync(factory, "reviewer");
        using var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { AltAction(Guid.NewGuid().ToString(), 1, chipId, 1) }
        });
        using var body = await JsonAsync(response);
        Assert.Equal("invalid", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray()).GetProperty("result").GetString());
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("abuela", (await database.Words.SingleAsync()).Headword);
            Assert.Empty(await database.WordReviews.ToListAsync());
            Assert.Single(await database.WordRevisions.ToListAsync());
        }
    }

    [Fact]
    public async Task NounGenderEditCannotBypassImportGrammarConstraint()
    {
        using var factory = new ReviewApplicationFactory(Now);
        await WriteSourceAsync(factory);
        await ImportSourceAsync(factory);
        using var reviewer = await LoginAsync(factory, "reviewer");
        using var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1,
                action = "approve_edit", edits = new[] { new { field = "gender", after = "not_applicable" } } } }
        });
        using var body = await JsonAsync(response);
        Assert.Equal("invalid", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray()).GetProperty("result").GetString());
        await using var database = factory.OpenDatabase();
        Assert.Equal("feminine", (await database.Words.SingleAsync()).Gender);
        Assert.Empty(await database.WordReviews.ToListAsync());
    }

    [Fact]
    public async Task DemotedQueuedAttemptReturnsStaleEpochAndDayDoneStillCommits()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved");
            AddCard(database, 10);
        });
        using var learner = await LoginAsync(factory, "learner");
        int cardId;
        await using (var database = factory.OpenDatabase()) cardId = (await database.Cards.SingleAsync()).Id;
        var queued = new { clientAttemptId = Guid.NewGuid().ToString(), cardId, cardEpoch = 0,
            sessionId = "queued-before-demotion", mode = "recognition", rating = 3,
            isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null, overridden = false,
            feedbackKind = (string?)null, answerText = (string?)null, durationMs = 900,
            reviewedAtUtc = Now.AddMinutes(-5) };
        using var reviewer = await LoginAsync(factory, "reviewer");
        using (var demote = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote") }
        })) Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        using (var attempt = await PostAsync(learner, "/api/learn/attempts", new { attempts = new[] { queued } }))
        {
            Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
            using var body = await JsonAsync(attempt);
            Assert.Equal("stale_epoch", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("status").GetString());
        }
        using (var done = await PostAsync(learner, "/api/learn/day/done", new
        {
            durationS = 640, plannedS = 600, newCount = 1, reviewCount = 0
        }))
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            using var body = await JsonAsync(done);
            Assert.Equal("done", body.RootElement.GetProperty("status").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.False((await database.ReviewLogs.SingleAsync()).CountsForSrs);
            Assert.Equal(0, (await database.Cards.SingleAsync()).Reps);
            var day = await database.DayStatuses.SingleAsync();
            Assert.Equal("done", day.Status);
            Assert.Equal("2026-10-19", day.LocalDate);
            Assert.Equal((await database.PracticeSessions.SingleAsync()).Id, day.SessionId);
            Assert.Empty(await database.DaySkips.ToListAsync());
        }
    }

    private static Task<ContentImportResult> ImportSourceAsync(ReviewApplicationFactory factory) =>
        new ContentImporter(new(factory.DatabasePath)).ImportAsync(factory.ImportDirectory, false, CancellationToken.None);

    private static void AssertImport(ContentImportResult result, int created, int updated, int unchanged)
    {
        Assert.Equal(created, result.Created);
        Assert.Equal(updated, result.Updated);
        Assert.Equal(unchanged, result.Unchanged);
        Assert.Empty(result.Rejected);
    }

    private static async Task WriteSourceAsync(ReviewApplicationFactory factory, string gloss = "grandmother")
    {
        Directory.CreateDirectory(factory.ImportDirectory);
        var root = JsonNode.Parse("""
            {"id":"es-abuela-01","headword":"abuela","article":"la","gender":"feminine",
             "partOfSpeech":"noun","cefr":"A1","region":{"variant":"es-ES","scope":"peninsular-generic"},
             "register":"neutral","topics":["family"],"senses":[{"gloss":"grandmother",
             "example":"Mi abuela vive aquí.","exampleGloss":"My grandmother lives here."}],
             "alternatives":[{"kind":"synonym","value":"abuelita","reason":"Más cariñoso y familiar"},
                             {"kind":"near-synonym","value":"anciana","reason":"Sin implicar parentesco"}],
             "status":"pending_review","provenance":{"method":"hand-authored"},"createdAt":"2026-10-19T08:00:00Z"}
            """)!;
        root["senses"]![0]!["gloss"] = gloss;
        await File.WriteAllTextAsync(Path.Combine(factory.ImportDirectory, "es-abuela-01.json"), root.ToJsonString());
    }
}
