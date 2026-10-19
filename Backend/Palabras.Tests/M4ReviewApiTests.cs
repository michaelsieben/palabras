using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Accounts;
using Palabras.Content;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

// M4 reviewer gate contract tests (plan §2.2/§2.4, T-20261019-1606 step 2). Every call goes through
// the real cookie auth, the X-Palabras middleware and the SQLite write-lock transaction. The
// database files live under /tmp/palabras-m4, never /data.
public sealed partial class M4ReviewApiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T09:00:00Z", CultureInfo.InvariantCulture);
    private const string Password = "test-password";

    [Fact]
    public async Task QueueListsPendingWordsInTopicThenAgreementThenKeyOrder()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", topic: "family", agreement: 0.9, article: "la",
                gender: "feminine", glosses: ["grandmother"], statusChangedAt: null,
                alternativeValues: [("synonym", "abuelita", "más cariñoso")]);
            AddWord(database, 2, "es-tia-02", "tía", topic: "family", agreement: null);
            AddWord(database, 3, "es-pan-03", "pan", topic: "food", agreement: 0.95);
            AddWord(database, 4, "es-mesa-04", "mesa", topic: "household", status: "approved");
        });
        using var client = await LoginAsync(factory, "reviewer");

        using var response = await client.GetAsync("/api/review/queue", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        // family (two pending) first, agreement DESC NULLS LAST then key; food next. The default
        // scope is pending-only, exactly like the pre-trust-by-default page, so the approved word is
        // not here: rendering it as a pending card would offer "approve" on an approved row.
        Assert.Equal(new[] { "es-abuela-01", "es-tia-02", "es-pan-03" },
            items.Select(item => item.GetProperty("key").GetString()).ToArray());
        Assert.All(items, item => Assert.Equal("pending_review", item.GetProperty("status").GetString()));
        Assert.Equal(3, body.RootElement.GetProperty("pendingTotal").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("approvedTotal").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("nextAfter", out _));
        var topics = body.RootElement.GetProperty("topics").EnumerateArray().ToArray();
        Assert.Equal(new[] { "family", "food" }, topics.Select(topic => topic.GetProperty("topic").GetString()).ToArray());
        Assert.Equal(new[] { 2, 1 }, topics.Select(topic => topic.GetProperty("pending").GetInt32()).ToArray());

        // The objection UI must ask for approved rows explicitly; `status=all` appends them after the
        // pending page so a reviewer can object to an already-approved word.
        using (var all = await JsonAsync(await client.GetAsync("/api/review/queue?status=all")))
        {
            var allItems = all.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "es-abuela-01", "es-tia-02", "es-pan-03", "es-mesa-04" },
                allItems.Select(item => item.GetProperty("key").GetString()).ToArray());
            Assert.Equal(new[] { "pending_review", "pending_review", "pending_review", "approved" },
                allItems.Select(item => item.GetProperty("status").GetString()).ToArray());
            Assert.Equal(3, all.RootElement.GetProperty("pendingTotal").GetInt32());
            Assert.Equal(1, all.RootElement.GetProperty("approvedTotal").GetInt32());
            Assert.False(all.RootElement.TryGetProperty("nextAfter", out _));
        }

        var abuela = items[0];
        Assert.Equal(1, abuela.GetProperty("wordId").GetInt32());
        Assert.Equal(1, abuela.GetProperty("version").GetInt32());
        Assert.Equal("abuela", abuela.GetProperty("headword").GetString());
        Assert.Equal("la", abuela.GetProperty("article").GetString());
        Assert.Equal("feminine", abuela.GetProperty("gender").GetString());
        Assert.Equal("noun", abuela.GetProperty("pos").GetString());
        Assert.Equal("A1", abuela.GetProperty("cefr").GetString());
        Assert.Equal("family", abuela.GetProperty("topic").GetString());
        Assert.Equal(0.9, abuela.GetProperty("agreement").GetDouble(), 3);
        Assert.False(abuela.TryGetProperty("band", out _));
        Assert.False(abuela.GetProperty("reducedConfidence").GetBoolean());
        Assert.Empty(abuela.GetProperty("lintWarnings").EnumerateArray());
        var sense = Assert.Single(abuela.GetProperty("senses").EnumerateArray());
        Assert.Equal(0, sense.GetProperty("order").GetInt32());
        Assert.Equal("grandmother", sense.GetProperty("gloss").GetString());
        Assert.Equal(new[] { "abuela" },
            sense.GetProperty("acceptedEs").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("Ejemplo de abuela.", sense.GetProperty("exampleEs").GetString());
        var chip = Assert.Single(abuela.GetProperty("alternatives").EnumerateArray());
        Assert.Equal("lemma", chip.GetProperty("field").GetString());
        Assert.Equal("abuelita", chip.GetProperty("text").GetString());
        Assert.Equal("más cariñoso", chip.GetProperty("reason").GetString());
        Assert.False(abuela.TryGetProperty("lastFlag", out _));

        using var limited = await client.GetAsync("/api/review/queue?limit=1", CancellationToken.None);
        using var limitedBody = await JsonAsync(limited);
        Assert.Equal(1, limitedBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(3, limitedBody.RootElement.GetProperty("pendingTotal").GetInt32());
        // The pending-only scope keeps its pre-change single page: it hands out no cursor, so it can
        // never promise a page it does not serve.
        Assert.False(limitedBody.RootElement.TryGetProperty("nextAfter", out _));
        foreach (var bad in new[] { "0", "31", "abc", "-1" })
        {
            using var rejected = await client.GetAsync("/api/review/queue?limit=" + bad, CancellationToken.None);
            await AssertJsonErrorAsync(rejected, HttpStatusCode.BadRequest, "bad_request");
        }
    }

    [Fact]
    public async Task QueueExposesApprovedRowsForObjectionAndPagesThemByWordId()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved");
            AddWord(database, 2, "es-pan-02", "pan", status: "approved", topic: "food");
            AddWord(database, 3, "es-mesa-03", "mesa", status: "approved", topic: "household");
            AddWord(database, 4, "es-tia-04", "tía", status: "pending_review");
        });
        using var client = await LoginAsync(factory, "reviewer");

        // status=pending keeps the exact pre-trust-by-default page.
        using (var pending = await JsonAsync(await client.GetAsync("/api/review/queue?status=pending")))
        {
            var item = Assert.Single(pending.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("es-tia-04", item.GetProperty("key").GetString());
            Assert.Equal(0, pending.RootElement.GetProperty("approvedTotal").GetInt32());
        }

        int firstPageLastId;
        using (var approved = await JsonAsync(await client.GetAsync("/api/review/queue?status=approved&limit=2")))
        {
            var items = approved.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.All(items, item => Assert.Equal("approved", item.GetProperty("status").GetString()));
            Assert.Equal(3, approved.RootElement.GetProperty("approvedTotal").GetInt32());
            firstPageLastId = items[^1].GetProperty("wordId").GetInt32();
            Assert.Equal(firstPageLastId, approved.RootElement.GetProperty("nextAfter").GetInt32());
        }
        using (var rest = await JsonAsync(await client.GetAsync(
            "/api/review/queue?status=approved&after=" + firstPageLastId)))
        {
            var item = Assert.Single(rest.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("approved", item.GetProperty("status").GetString());
            Assert.False(rest.RootElement.TryGetProperty("nextAfter", out _));
        }
        using (var bad = await client.GetAsync("/api/review/queue?status=bogus", CancellationToken.None))
        {
            await AssertJsonErrorAsync(bad, HttpStatusCode.BadRequest, "bad_request");
        }
        using (var badCursor = await client.GetAsync("/api/review/queue?status=approved&after=999",
            CancellationToken.None))
        {
            await AssertJsonErrorAsync(badCursor, HttpStatusCode.BadRequest, "bad_request");
        }
    }

    [Fact]
    public async Task QueueScopesAreMutuallyExclusiveAndAllIncludesRejectedRows()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved",
                statusChangedAt: Now.AddHours(-1).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddWord(database, 2, "es-pan-02", "pan", status: "rejected", topic: "food",
                statusChangedAt: Now.AddHours(-2).ToUnixTimeMilliseconds());
            AddWord(database, 3, "es-tia-03", "tía");
            AddWord(database, 4, "es-mesa-04", "mesa", status: "rejected", topic: "household",
                statusChangedAt: Now.AddHours(-3).ToUnixTimeMilliseconds());
            AddWord(database, 5, "es-casa-05", "casa", status: "approved", topic: "household",
                statusChangedAt: Now.AddHours(-4).ToUnixTimeMilliseconds(), approvedVersion: 1);
        });
        using var client = await LoginAsync(factory, "reviewer");

        // The default stays pending-only; `status=pending` is the same page spelled out.
        foreach (var path in new[] { "/api/review/queue", "/api/review/queue?status=pending" })
        {
            using var body = await JsonAsync(await client.GetAsync(path));
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("es-tia-03", item.GetProperty("key").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("pendingTotal").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("approvedTotal").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("rejectedTotal").GetInt32());
        }

        using (var approved = await JsonAsync(await client.GetAsync("/api/review/queue?status=approved")))
        {
            var items = approved.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "es-abuela-01", "es-casa-05" },
                items.Select(item => item.GetProperty("key").GetString()).ToArray());
            Assert.All(items, item => Assert.Equal("approved", item.GetProperty("status").GetString()));
            Assert.Equal(0, approved.RootElement.GetProperty("pendingTotal").GetInt32());
            Assert.Equal(2, approved.RootElement.GetProperty("approvedTotal").GetInt32());
            Assert.Equal(0, approved.RootElement.GetProperty("rejectedTotal").GetInt32());
        }

        // `status=rejected` is the withdrawn list the objection UI needs: newest decision first,
        // and each row carries the action contract (wordId + version), so re-approving needs no guess.
        using (var rejected = await JsonAsync(await client.GetAsync("/api/review/queue?status=rejected")))
        {
            var items = rejected.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "es-pan-02", "es-mesa-04" },
                items.Select(item => item.GetProperty("key").GetString()).ToArray());
            Assert.All(items, item => Assert.Equal("rejected", item.GetProperty("status").GetString()));
            Assert.Equal(2, items[0].GetProperty("wordId").GetInt32());
            Assert.Equal(1, items[0].GetProperty("version").GetInt32());
            Assert.Equal(0, rejected.RootElement.GetProperty("pendingTotal").GetInt32());
            Assert.Equal(0, rejected.RootElement.GetProperty("approvedTotal").GetInt32());
            Assert.Equal(2, rejected.RootElement.GetProperty("rejectedTotal").GetInt32());
            Assert.Equal(0, rejected.RootElement.GetProperty("topics").GetArrayLength());
        }

        // `all` is the union of the three statuses, pending first and then newest decision first.
        using (var all = await JsonAsync(await client.GetAsync("/api/review/queue?status=all")))
        {
            var items = all.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "es-tia-03", "es-abuela-01", "es-pan-02", "es-mesa-04", "es-casa-05" },
                items.Select(item => item.GetProperty("key").GetString()).ToArray());
            Assert.Equal(new[] { "pending_review", "approved", "rejected", "rejected", "approved" },
                items.Select(item => item.GetProperty("status").GetString()).ToArray());
            Assert.Equal(1, all.RootElement.GetProperty("pendingTotal").GetInt32());
            Assert.Equal(2, all.RootElement.GetProperty("approvedTotal").GetInt32());
            Assert.Equal(2, all.RootElement.GetProperty("rejectedTotal").GetInt32());
        }

        // The rejected scope pages with the same keyset cursor as the approved scope.
        using (var page = await JsonAsync(await client.GetAsync("/api/review/queue?status=rejected&limit=1")))
        {
            var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("es-pan-02", item.GetProperty("key").GetString());
            Assert.Equal(2, page.RootElement.GetProperty("nextAfter").GetInt32());
        }
        using (var rest = await JsonAsync(await client.GetAsync("/api/review/queue?status=rejected&after=2")))
        {
            var item = Assert.Single(rest.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("es-mesa-04", item.GetProperty("key").GetString());
            Assert.False(rest.RootElement.TryGetProperty("nextAfter", out _));
        }

        foreach (var bad in new[] { "bogus", "draft", "retired", "ALL" })
        {
            using var response = await client.GetAsync("/api/review/queue?status=" + bad, CancellationToken.None);
            await AssertJsonErrorAsync(response, HttpStatusCode.BadRequest, "bad_request");
        }
    }

    [Fact]
    public async Task QueueAllScopeReachesDecidedRowsWhenPendingFillsThePageExactly()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", agreement: 0.9);
            AddWord(database, 2, "es-pan-02", "pan", agreement: 0.8);
            AddWord(database, 3, "es-mesa-03", "mesa", agreement: 0.7);
            AddWord(database, 4, "es-silla-04", "silla", status: "approved",
                statusChangedAt: Now.AddHours(-1).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddWord(database, 5, "es-libro-05", "libro", status: "rejected",
                statusChangedAt: Now.AddHours(-2).ToUnixTimeMilliseconds());
            AddWord(database, 6, "es-lápiz-06", "lápiz", status: "rejected",
                statusChangedAt: Now.AddHours(-3).ToUnixTimeMilliseconds());
        });
        using var client = await LoginAsync(factory, "reviewer");

        // limit == pending count: page one is pending-only (the decided budget is 0), but it must
        // still hand out a cursor, and the next page must start the decided list. Before step 41 the
        // walk stopped there and the rejected rows were unreachable through `status=all`.
        var pages = await WalkAllScopeAsync(client, limit: 3);
        Assert.Equal(new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 } },
            pages.Select(page => page.Select(row => row.WordId).ToArray()).ToArray());

        var walked = pages.SelectMany(page => page).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, walked.Select(row => row.WordId).OrderBy(id => id).ToArray());
        Assert.Equal(walked.Length, walked.Select(row => row.WordId).Distinct().Count());
        Assert.Equal(new[] { "pending_review", "pending_review", "pending_review", "approved", "rejected", "rejected" },
            walked.Select(row => row.Status).ToArray());

        // The second page repeats no pending row and carries the rejected rows with their action
        // contract (wordId + version), so a withdrawn word stays reachable through `all`.
        Assert.DoesNotContain(pages[1], row => pages[0].Any(first => first.WordId == row.WordId));
        Assert.Contains(walked, row => row.WordId == 5 && row.Status == "rejected");
        Assert.Contains(walked, row => row.WordId == 6 && row.Status == "rejected");
    }

    [Fact]
    public async Task QueueAllScopeWalksPendingAndDecidedPagesWithoutDuplicatesOrGaps()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            // More pending rows than one page holds, plus decided rows of both kinds: the walk must
            // finish the pending block first, then continue into the decided list.
            AddWord(database, 1, "es-abuela-01", "abuela", agreement: 0.9);
            AddWord(database, 2, "es-pan-02", "pan", agreement: 0.8);
            AddWord(database, 3, "es-mesa-03", "mesa", agreement: 0.7);
            AddWord(database, 4, "es-tía-04", "tía", agreement: 0.6);
            AddWord(database, 5, "es-casa-05", "casa", agreement: 0.5);
            AddWord(database, 6, "es-silla-06", "silla", status: "approved",
                statusChangedAt: Now.AddHours(-1).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddWord(database, 7, "es-libro-07", "libro", status: "rejected",
                statusChangedAt: Now.AddHours(-2).ToUnixTimeMilliseconds());
            AddWord(database, 8, "es-verbo-08", "verbo", status: "approved",
                statusChangedAt: Now.AddHours(-3).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddWord(database, 9, "es-lápiz-09", "lápiz", status: "rejected",
                statusChangedAt: Now.AddHours(-4).ToUnixTimeMilliseconds());
        });
        using var client = await LoginAsync(factory, "reviewer");

        var pages = await WalkAllScopeAsync(client, limit: 3);
        // pending order is agreement DESC, decided order is newest decision first; page two mixes the
        // tail of the pending block with the first decided row.
        Assert.Equal(
            new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } },
            pages.Select(page => page.Select(row => row.WordId).ToArray()));

        var walked = pages.SelectMany(page => page).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            walked.Select(row => row.WordId).OrderBy(id => id).ToArray());
        Assert.Equal(walked.Length, walked.Select(row => row.WordId).Distinct().Count());
        Assert.Equal(5, walked.Count(row => row.Status == "pending_review"));
        Assert.Equal(2, walked.Count(row => row.Status == "approved"));
        Assert.Equal(2, walked.Count(row => row.Status == "rejected"));

        // A later page never repeats a pending row, and both rejected rows are reached.
        Assert.DoesNotContain(pages[1], row => pages[0].Any(first => first.WordId == row.WordId));
        Assert.DoesNotContain(pages[2], row => pages[0].Concat(pages[1]).Any(earlier => earlier.WordId == row.WordId));
        Assert.Contains(walked, row => row.WordId == 7 && row.Status == "rejected");
        Assert.Contains(walked, row => row.WordId == 9 && row.Status == "rejected");
    }

    [Fact]
    public async Task RejectedWordIsDiscoverableInTheQueueAndCanBeReapprovedFromThatRow()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved", article: "la",
                gender: "feminine", statusChangedAt: Now.AddDays(-3).ToUnixTimeMilliseconds(),
                approvedVersion: 1);
            AddCard(database, 10, state: CardState.Review, epoch: 0, reps: 4, lapses: 1, stability: 12.5,
                difficulty: 6.2, dueMs: Now.AddDays(2).ToUnixTimeMilliseconds(),
                lastReviewMs: Now.AddDays(-1).ToUnixTimeMilliseconds());
        });
        using var client = await LoginAsync(factory, "reviewer");
        await using (var database = factory.OpenDatabase())
        {
            database.ReviewLogs.Add(new ReviewLog
            {
                UserId = 1, WordId = 1, CardId = (await database.Cards.OrderBy(card => card.Id)
                    .Select(card => card.Id).FirstAsync()), CardEpoch = 0,
                ClientAttemptId = "before-objection", Rating = Rating.Good,
                ReviewedAtMs = Now.AddDays(-1).ToUnixTimeMilliseconds(),
                ReceivedAtMs = Now.AddDays(-1).ToUnixTimeMilliseconds(), CountsForSrs = true
            });
            await database.SaveChangesAsync();
        }

        // 1) The reviewer objects to the approved word.
        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "reject", note: "No es peninsular") }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        // 2) The withdrawn word is reachable on both server-backed scopes, with the current version.
        int wordId;
        int version;
        using (var rejected = await JsonAsync(await client.GetAsync("/api/review/queue?status=rejected")))
        {
            var item = Assert.Single(rejected.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("rejected", item.GetProperty("status").GetString());
            Assert.Equal("es-abuela-01", item.GetProperty("key").GetString());
            wordId = item.GetProperty("wordId").GetInt32();
            version = item.GetProperty("version").GetInt32();
            Assert.Equal(1, rejected.RootElement.GetProperty("rejectedTotal").GetInt32());
        }
        using (var all = await JsonAsync(await client.GetAsync("/api/review/queue?status=all")))
        {
            var item = Assert.Single(all.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("rejected", item.GetProperty("status").GetString());
            Assert.Equal(wordId, item.GetProperty("wordId").GetInt32());
            Assert.Equal(version, item.GetProperty("version").GetInt32());
        }

        // 3) approve with the version the rejected row handed out: the objection is lifted.
        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "approve", wordVersion: version) }
        }))
        {
            using var body = await JsonAsync(response);
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("applied", result.GetProperty("result").GetString());
            Assert.Equal("approved", result.GetProperty("word").GetProperty("status").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("approved", (await database.Words.SingleAsync()).Status);
            // The pre-objection card is reset; the missing direction is created fresh by the approval.
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.Equal(2, cards.Count);
            var card = cards[0];
            Assert.Null(card.SuspendedReason);
            Assert.Equal(2, card.Epoch); // rejection +1, objection-withdrawn reset +1
            Assert.Equal(CardState.New, card.State);
            Assert.Equal(0, card.Reps);
            Assert.Null(card.Stability);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), card.DueMs);
            Assert.Null(cards[1].SuspendedReason);
            // No history deleted and both verdicts still recorded.
            Assert.Single(await database.ReviewLogs.ToListAsync());
            Assert.Equal(new[] { "reject", "approve" },
                (await database.WordReviews.OrderBy(review => review.Id).ToListAsync())
                    .Select(review => review.Action).ToArray());
            Assert.Empty(await database.Words.Where(word => word.Status == "rejected").ToListAsync());
        }
    }

    [Fact]
    public async Task QueueShowsBandLintAndTheLastNeedsChangeFlag()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-coger-01", "coger", band: "media", lint: "[\"LatAm meaning\"]",
                agreement: 0.6, reducedConfidence: 1);
            database.WordReviews.Add(new WordReview
            {
                WordId = 1, WordVersion = 1, ReviewerId = 2, Action = "needs_change",
                Note = "Elige otra glosa", ClientReviewId = Guid.NewGuid().ToString("D"),
                CreatedAt = Now.AddHours(-2).ToUnixTimeMilliseconds()
            });
        });
        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("media", item.GetProperty("band").GetString());
        Assert.True(item.GetProperty("reducedConfidence").GetBoolean());
        Assert.Equal(new[] { "LatAm meaning" },
            item.GetProperty("lintWarnings").EnumerateArray().Select(value => value.GetString()).ToArray());
        var flag = item.GetProperty("lastFlag");
        Assert.Equal("needs_change", flag.GetProperty("action").GetString());
        Assert.Equal("Elige otra glosa", flag.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ApproveMarksTheWordApprovedWritesOneVerdictAndCreatesOwnerCardsOnly()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", article: "la", gender: "feminine", agreement: 0.9);
        });
        using var client = await LoginAsync(factory, "reviewer");
        var clientReviewId = Guid.NewGuid().ToString();
        using var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId, wordId = 1, wordVersion = 1, action = "approve", field = (string?)null,
                    senseOrder = (int?)null, alternativeId = (int?)null, edits = (object?)null,
                    note = "Vale", decisionMs = 2300, viaBatch = (bool?)null
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(clientReviewId, result.GetProperty("clientReviewId").GetString());
        Assert.Equal("applied", result.GetProperty("result").GetString());
        Assert.Equal("approved", result.GetProperty("word").GetProperty("status").GetString());
        Assert.Equal(1, result.GetProperty("word").GetProperty("version").GetInt32());

        await using var database = factory.OpenDatabase();
        var word = await database.Words.SingleAsync(candidate => candidate.Id == 1);
        Assert.Equal("approved", word.Status);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), word.StatusChangedAt);
        Assert.Equal(1, word.ApprovedVersion);
        Assert.Equal(1, word.Version);
        var revision = await database.WordRevisions.SingleAsync(candidate => candidate.Action == "approve");
        Assert.Equal(1, revision.Version);
        Assert.Equal(2, revision.ActorUserId);
        Assert.Equal("approved", JsonNode.Parse(revision.SnapshotJson)!["status"]!.GetValue<string>());
        var verdict = await database.WordReviews.SingleAsync();
        Assert.Equal(1, verdict.WordId);
        Assert.Equal(1, verdict.WordVersion);
        Assert.Equal(2, verdict.ReviewerId);
        Assert.Equal("approve", verdict.Action);
        Assert.Equal(clientReviewId, verdict.ClientReviewId);
        Assert.Equal("Vale", verdict.Note);
        Assert.Equal(0, verdict.ViaBatch);
        Assert.Equal(2300, verdict.DecisionMs);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), verdict.CreatedAt);
        Assert.Equal(0.9, verdict.AgreementAtReview ?? 0, 3);
        // The owner (learner role) gets both directions; the reviewer is not a learner.
        var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, card => Assert.Equal(1, card.UserId));
        Assert.Equal(new[] { "en_es", "es_en" }, cards.Select(card => card.Direction).Order().ToArray());
        Assert.DoesNotContain(cards, card => card.UserId == 2);
    }

    [Fact]
    public async Task NeedsChangeAndRejectKeepContentAndWriteTheirOwnRevision()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-pan-02", "pan", topic: "food");
        });
        using var client = await LoginAsync(factory, "reviewer");
        using var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1,
                    action = "needs_change", field = "gloss", note = "Falta el acento"
                },
                Action(Guid.NewGuid().ToString(), 2, "reject", note: "No es peninsular")
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.All(results, row => Assert.Equal("applied", row.GetProperty("result").GetString()));
            Assert.Equal(new[] { "needs_change", "rejected" },
                results.Select(row => row.GetProperty("word").GetProperty("status").GetString()).ToArray());
        }
        await using var database = factory.OpenDatabase();
        Assert.Equal(new[] { "needs_change", "rejected" },
            (await database.Words.OrderBy(word => word.Id).ToListAsync()).Select(word => word.Status).ToArray());
        Assert.All(await database.Words.ToListAsync(), word =>
        {
            Assert.Equal(1, word.Version);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), word.StatusChangedAt);
            Assert.Null(word.ApprovedVersion);
        });
        Assert.Equal(new[] { "needs_change", "reject" },
            (await database.WordRevisions.Where(revision => revision.Action != "import")
                .OrderBy(revision => revision.Id).ToListAsync()).Select(revision => revision.Action).ToArray());
        Assert.Empty(await database.Cards.ToListAsync());
        Assert.Equal(2, await database.WordReviews.CountAsync());
        // "Cambiar ⚑" names the field it is flagging; reject leaves it null.
        Assert.Equal(new[] { "gloss", null }, (await database.WordReviews.OrderBy(review => review.Id).ToListAsync())
            .Select(review => review.Field).ToArray());
    }

    [Fact]
    public async Task DemoteSuspendsCardsWithoutTouchingFsrsAndReapprovalReleasesThem()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved",
                statusChangedAt: Now.AddDays(-3).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddCard(database, 10, state: CardState.Review, epoch: 0, reps: 4, lapses: 1, stability: 12.5,
                difficulty: 6.2, dueMs: Now.AddDays(2).ToUnixTimeMilliseconds(),
                lastReviewMs: Now.AddDays(-1).ToUnixTimeMilliseconds());
            AddCard(database, 10, direction: "en_es", state: CardState.New, epoch: 0);
        });
        using var client = await LoginAsync(factory, "reviewer");

        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote", note: "Revisar otra vez") }
        }))
        {
            using var body = await JsonAsync(response);
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("applied", result.GetProperty("result").GetString());
            Assert.Equal("pending_review", result.GetProperty("word").GetProperty("status").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            Assert.Equal("pending_review", word.Status);
            Assert.Equal(1, word.Version);
            Assert.Equal(1, word.ApprovedVersion);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), word.StatusChangedAt);
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.All(cards, card => Assert.Equal(1, card.Epoch));
            Assert.All(cards, card => Assert.Equal("word_not_approved", card.SuspendedReason));
            // FSRS state is content-independent and must survive the demotion.
            Assert.Equal(4, cards[0].Reps);
            Assert.Equal(1, cards[0].Lapses);
            Assert.Equal(12.5, cards[0].Stability);
            Assert.Equal(6.2, cards[0].Difficulty);
            Assert.Equal(Now.AddDays(2).ToUnixTimeMilliseconds(), cards[0].DueMs);
            Assert.Equal(Now.AddDays(-1).ToUnixTimeMilliseconds(), cards[0].LastReviewMs);
            Assert.Equal(CardState.Review, cards[0].State);
            Assert.Equal("demote", (await database.WordRevisions.SingleAsync(
                revision => revision.Action == "demote")).Action);
        }

        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "approve") }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("approved", (await database.Words.SingleAsync()).Status);
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.All(cards, card => Assert.Equal(2, card.Epoch));
            Assert.All(cards, card => Assert.Null(card.SuspendedReason));
            Assert.Equal(4, cards[0].Reps);
            Assert.Equal(12.5, cards[0].Stability);
            Assert.Equal(CardState.Review, cards[0].State);
            Assert.Equal(0, cards[1].Reps);
        }
    }

    [Fact]
    public async Task RejectAnApprovedWordSuspendsCardsKeepsHistoryAndUndoStartsThemFresh()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved",
                statusChangedAt: Now.AddDays(-3).ToUnixTimeMilliseconds(), approvedVersion: 1);
            AddCard(database, 10, state: CardState.Review, epoch: 0, reps: 4, lapses: 1, stability: 12.5,
                difficulty: 6.2, dueMs: Now.AddDays(2).ToUnixTimeMilliseconds(),
                lastReviewMs: Now.AddDays(-1).ToUnixTimeMilliseconds());
            AddCard(database, 10, direction: "en_es", state: CardState.New, epoch: 0);
        });
        using var client = await LoginAsync(factory, "reviewer");
        await using (var database = factory.OpenDatabase())
        {
            database.ReviewLogs.Add(new ReviewLog
            {
                UserId = 1, WordId = 1, CardId = (await database.Cards.OrderBy(card => card.Id)
                    .Select(card => card.Id).FirstAsync()), CardEpoch = 0,
                ClientAttemptId = "before-objection", Rating = Rating.Good,
                ReviewedAtMs = Now.AddDays(-1).ToUnixTimeMilliseconds(),
                ReceivedAtMs = Now.AddDays(-1).ToUnixTimeMilliseconds(), CountsForSrs = true
            });
            await database.SaveChangesAsync();
        }

        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "reject", note: "No es peninsular") }
        }))
        {
            using var body = await JsonAsync(response);
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("applied", result.GetProperty("result").GetString());
            Assert.Equal("rejected", result.GetProperty("word").GetProperty("status").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("rejected", (await database.Words.SingleAsync()).Status);
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.All(cards, card =>
            {
                Assert.Equal("word_not_approved", card.SuspendedReason);
                Assert.Equal(1, card.Epoch);
            });
            // Deactivation does not throw away the learning history or the FSRS state yet.
            Assert.Equal(4, cards[0].Reps);
            Assert.Single(await database.ReviewLogs.ToListAsync());
            Assert.Equal("reject", (await database.WordReviews.SingleAsync()).Action);
            Assert.Single(await database.WordRevisions.Where(revision => revision.Action == "reject").ToListAsync());
        }

        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "approve") }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("approved", (await database.Words.SingleAsync()).Status);
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.All(cards, card =>
            {
                Assert.Null(card.SuspendedReason);
                Assert.Equal(2, card.Epoch); // rejection +1, objection-withdrawn reset +1
                Assert.Equal(CardState.New, card.State);
                Assert.Equal(0, card.Reps);
                Assert.Equal(0, card.Lapses);
                Assert.Null(card.Stability);
                Assert.Null(card.Difficulty);
                Assert.Null(card.LastReviewMs);
                Assert.Equal(Now.ToUnixTimeMilliseconds(), card.DueMs);
            });
            // Review history survives the round trip.
            Assert.Single(await database.ReviewLogs.ToListAsync());
            Assert.Equal(new[] { "reject", "approve" },
                (await database.WordReviews.OrderBy(review => review.Id).ToListAsync())
                    .Select(review => review.Action).ToArray());
        }
    }

    [Fact]
    public async Task ApproveEditWritesANewSnapshotAppliesTheEditToLearnerReadsAndSupportsUndo()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved", article: "la",
                statusChangedAt: Now.AddDays(-3).ToUnixTimeMilliseconds(), approvedVersion: 1,
                glosses: ["grandmother", "old woman"]);
            AddCard(database, 10, state: CardState.Review, epoch: 0, reps: 3, stability: 9);
            AddCard(database, 10, direction: "en_es", state: CardState.New, epoch: 0);
        });
        using var reviewer = await LoginAsync(factory, "reviewer");
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote") }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var clientReviewId = Guid.NewGuid().ToString();
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId, wordId = 1, wordVersion = 1, action = "approve_edit", field = (string?)null,
                    senseOrder = (int?)null, alternativeId = (int?)null,
                    edits = new object[]
                    {
                        new { field = "gloss", senseOrder = 0, after = "grandmother (edited)" },
                        new { field = "example", senseOrder = 0, after = "Mi abuela vive aquí." }
                    },
                    note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)null
                }
            }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await JsonAsync(response);
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("applied", result.GetProperty("result").GetString());
            Assert.Equal("approved", result.GetProperty("word").GetProperty("status").GetString());
            Assert.Equal(2, result.GetProperty("word").GetProperty("version").GetInt32());
        }

        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            Assert.Equal("approved", word.Status);
            Assert.Equal(2, word.Version);
            Assert.Equal(2, word.ApprovedVersion);
            Assert.Equal("grandmother (edited)",
                (await database.Senses.SingleAsync(sense => sense.SenseOrder == 0)).EnGloss);
            var revisions = await database.WordRevisions.Where(revision => revision.Version == 2)
                .OrderBy(revision => revision.Id).ToListAsync();
            Assert.Equal(new[] { "edit", "approve" }, revisions.Select(revision => revision.Action).ToArray());
            var editSnapshot = JsonNode.Parse(revisions[0].SnapshotJson)!;
            Assert.Equal("grandmother (edited)", editSnapshot["senses"]![0]!["gloss"]!.GetValue<string>());
            Assert.Equal("Mi abuela vive aquí.", editSnapshot["senses"]![0]!["example"]!.GetValue<string>());
            var verdict = await database.WordReviews.SingleAsync(review => review.Action == "approve_edit");
            Assert.Equal("whole", verdict.Field);
            var diff = JsonNode.Parse(verdict.EditDiff!)!.AsArray();
            Assert.Equal(2, diff.Count);
            Assert.Equal("gloss", diff[0]!["field"]!.GetValue<string>());
            Assert.Equal("grandmother", diff[0]!["before"]!.GetValue<string>());
            // The content edit bumps the epoch again: the demotion already bumped it once. Cards
            // created by the approval start at 0; the pre-existing answers are invalidated (2).
            var cards = await database.Cards.OrderBy(card => card.Id).ToListAsync();
            Assert.Equal(4, cards.Count);
            Assert.All(cards.Take(2), card => Assert.Equal(2, card.Epoch));
            Assert.All(cards.Skip(2), card => Assert.Equal(0, card.Epoch));
            var previous = await database.WordAlternatives.SingleAsync(
                alternative => alternative.Kind == "previous_gloss");
            Assert.Equal("grandmother", previous.Value);
            Assert.Equal(0, previous.Rank);
        }

        // Learner visibility: the new current-version snapshot keeps the word active and shows the
        // edited gloss.
        using (var owner = await LoginAsync(factory, "learner"))
        using (var body = await JsonAsync(await owner.GetAsync("/api/learn/words", CancellationToken.None)))
        {
            var word = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("grandmother (edited)", word.GetProperty("gloss").GetString());
        }

        // Undo path (Q-M4-2): the previous value is stored as a chip and approve_alt restores it.
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote", wordVersion: 2) }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        int previousChipId;
        await using (var database = factory.OpenDatabase())
        {
            previousChipId = (await database.WordAlternatives.SingleAsync(
                alternative => alternative.Kind == "previous_gloss")).Id;
        }
        using (var response = await PostAsync(reviewer, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 2, action = "approve_alt",
                    field = (string?)null, senseOrder = 0, alternativeId = (int?)previousChipId,
                    edits = (object?)null, note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)null
                }
            }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("grandmother", (await database.Senses.SingleAsync(sense => sense.SenseOrder == 0)).EnGloss);
            Assert.Equal(3, (await database.Words.SingleAsync()).Version);
            var verdict = await database.WordReviews.SingleAsync(review => review.Action == "approve_alt");
            Assert.Equal("gloss", verdict.Field);
            Assert.Equal(previousChipId, verdict.ChosenAlternativeId);
        }
    }

    [Fact]
    public async Task ApproveAltSwapsTheHeadwordAndKeepsTheReplacedValue()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", alternativeValues: [("synonym", "abuelita", "más cariñoso")]);
        });
        int chipId;
        await using (var database = factory.OpenDatabase())
        {
            chipId = (await database.WordAlternatives.SingleAsync()).Id;
        }
        using var client = await LoginAsync(factory, "reviewer");
        using var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_alt",
                    field = (string?)null, senseOrder = (int?)null, alternativeId = (int?)chipId,
                    edits = (object?)null, note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)null
                }
            }
        });
        using (var body = await JsonAsync(response))
        {
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync();
            Assert.Equal("abuelita", word.Headword);
            Assert.Equal(2, word.Version);
            var snapshot = JsonNode.Parse((await database.WordRevisions
                .SingleAsync(revision => revision.Action == "approve")).SnapshotJson)!;
            Assert.Equal("abuelita", snapshot["headword"]!.GetValue<string>());
            var verdict = await database.WordReviews.SingleAsync();
            Assert.Equal("lemma", verdict.Field);
            Assert.Equal(chipId, verdict.ChosenAlternativeId);
            Assert.Equal("abuela", (await database.WordAlternatives.SingleAsync(
                alternative => alternative.Kind == "previous_lemma")).Value);
        }
    }

    [Fact]
    public async Task EveryIllegalTransitionIsRejectedPerItemAndChangesNothing()
    {
        var statuses = new[] { "draft", "pending_review", "approved", "needs_change", "rejected", "retired" };
        var actions = new[] { "approve", "approve_alt", "approve_edit", "needs_change", "reject", "demote" };
        // Plan §2.2 amended by trust-by-default (T-20261019-2100): pending_review actions are
        // unchanged; an approved word can be objected to (reject) and a rejected word re-approved
        // (undo); approved words also accept reviewer corrections (approve_edit/approve_alt).
        static bool Allowed(string status, string action) => (status, action) switch
        {
            ("pending_review", "approve" or "approve_alt" or "approve_edit" or "needs_change" or "reject") => true,
            ("approved", "demote" or "reject" or "approve_edit" or "approve_alt") => true,
            ("rejected", "approve") => true,
            _ => false
        };
        var rows = statuses.SelectMany(status => actions.Select(action => (Status: status, Action: action))).ToArray();
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            for (var index = 0; index < rows.Length; index++)
            {
                var wordId = index + 1;
                AddWord(database, wordId, $"es-word-{wordId:D2}", $"palabra{wordId}", status: rows[index].Status,
                    alternativeValues: [("synonym", "sinónimo" + wordId, "variante")]);
            }
        });
        using var client = await LoginAsync(factory, "reviewer");
        Dictionary<int, int> chipIds;
        await using (var seeded = factory.OpenDatabase())
        {
            chipIds = await seeded.WordAlternatives.ToDictionaryAsync(alternative => alternative.WordId,
                alternative => alternative.Id);
        }
        var requests = new List<object>();
        for (var index = 0; index < rows.Length; index++)
        {
            var wordId = index + 1;
            var action = rows[index].Action;
            requests.Add(action switch
            {
                "approve_alt" => AltAction(Guid.NewGuid().ToString(), wordId, chipId: chipIds[wordId], wordVersion: 1),
                "approve_edit" => EditAction(Guid.NewGuid().ToString(), wordId, after: "cambiada" + wordId),
                _ => Action(Guid.NewGuid().ToString(), wordId, action)
            });
        }
        using var response = await PostAsync(client, "/api/review/actions", new { actions = requests.ToArray() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(rows.Length, results.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                var expected = Allowed(rows[index].Status, rows[index].Action) ? "applied" : "invalid_transition";
                Assert.Equal(expected, results[index].GetProperty("result").GetString());
            }
        }
        await using (var database = factory.OpenDatabase())
        {
            var words = await database.Words.OrderBy(word => word.Id).ToListAsync();
            for (var index = 0; index < rows.Length; index++)
            {
                var (status, _) = rows[index];
                var expected = Allowed(status, rows[index].Action)
                    ? rows[index].Action == "demote" ? "pending_review" : rows[index].Action
                        is "reject" ? "rejected" : rows[index].Action is "needs_change" ? "needs_change" : "approved"
                    : status;
                Assert.Equal(expected, words[index].Status);
            }
            // Rejected transitions neither write a verdict nor a revision. approve_alt/approve_edit
            // write TWO revisions each (edit snapshot + approve at the new version).
            var applied = rows.Count(row => Allowed(row.Status, row.Action));
            var contentActions = rows.Count(row => Allowed(row.Status, row.Action)
                && row.Action is "approve_alt" or "approve_edit");
            Assert.Equal(applied, await database.WordReviews.CountAsync());
            Assert.Equal(applied + contentActions,
                await database.WordRevisions.CountAsync(revision => revision.Action != "import"));
        }
    }

    [Fact]
    public async Task StaleVersionAndReplayedReviewIdsAreRejectedBeforeAnyWrite()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-pan-02", "pan", topic: "food");
        });
        using var client = await LoginAsync(factory, "reviewer");
        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "approve", wordVersion: 7) }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("stale_version", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("pending_review", (await database.Words.SingleAsync(word => word.Id == 1)).Status);
            Assert.Empty(await database.WordReviews.ToListAsync());
            Assert.Equal(0, await database.WordRevisions.CountAsync(revision => revision.Action != "import"));
        }

        var clientReviewId = Guid.NewGuid().ToString();
        using (var first = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(clientReviewId, 2, "approve") }
        }))
        {
            using var body = await JsonAsync(first);
            Assert.Equal("applied", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        // Same id in a second request and twice inside one batch: both are duplicates.
        using (var replay = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(clientReviewId, 2, "approve") }
        }))
        {
            using var body = await JsonAsync(replay);
            Assert.Equal("duplicate", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var replay = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(clientReviewId, 2, "approve"), Action(clientReviewId, 2, "approve") }
        }))
        {
            using var body = await JsonAsync(replay);
            Assert.Equal(new[] { "duplicate", "duplicate" },
                body.RootElement.GetProperty("results").EnumerateArray()
                    .Select(row => row.GetProperty("result").GetString()).ToArray());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal(1, await database.WordReviews.CountAsync());
            Assert.Equal(1, await database.WordRevisions.CountAsync(revision => revision.Action == "approve"));
            Assert.Equal(2, await database.Cards.CountAsync());
        }
    }

    [Fact]
    public async Task BatchApproveCapsViaBatchAndRequiresHighAgreementWithoutLint()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-alta-01", "alta", band: "alta", agreement: 0.95);
            AddWord(database, 2, "es-media-02", "media", band: "media", agreement: 0.6);
            AddWord(database, 3, "es-lint-03", "lint", band: "alta", lint: "[\"denylist: puta\"]");
            AddWord(database, 4, "es-manual-04", "manual");
        });
        using var client = await LoginAsync(factory, "reviewer");
        using (var tooMany = await PostAsync(client, "/api/review/actions", new
        {
            actions = Enumerable.Range(0, 26)
                .Select(index => BatchAction(Guid.NewGuid().ToString(), 1)).ToArray()
        }))
        {
            await AssertJsonErrorAsync(tooMany, HttpStatusCode.BadRequest, "bad_request");
        }
        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                BatchAction(Guid.NewGuid().ToString(), 1),
                BatchAction(Guid.NewGuid().ToString(), 2),
                BatchAction(Guid.NewGuid().ToString(), 3),
                BatchAction(Guid.NewGuid().ToString(), 4)
            }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await JsonAsync(response);
            Assert.Equal(new[] { "applied", "invalid", "invalid", "invalid" },
                body.RootElement.GetProperty("results").EnumerateArray()
                    .Select(row => row.GetProperty("result").GetString()).ToArray());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("approved", (await database.Words.SingleAsync(word => word.Id == 1)).Status);
            var verdict = await database.WordReviews.SingleAsync();
            Assert.Equal(1, verdict.ViaBatch);
            Assert.Equal(new[] { "pending_review", "pending_review", "pending_review" },
                (await database.Words.Where(word => word.Id != 1).OrderBy(word => word.Id)
                    .ToListAsync()).Select(word => word.Status).ToArray());
        }
        // viaBatch is only meaningful for plain approvals.
        using (var wrongAction = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = 2, wordVersion = 1, action = "reject",
                    field = (string?)null, senseOrder = (int?)null, alternativeId = (int?)null,
                    edits = (object?)null, note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)true
                }
            }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, wrongAction.StatusCode);
            using var body = await JsonAsync(wrongAction);
            Assert.Equal("invalid", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
    }

    [Fact]
    public async Task UnusableItemsArePerItemInvalidAndBatchShapeErrorsAre400()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", alternativeValues: [("synonym", "abuelita", "cariño")]);
            AddWord(database, 2, "es-pan-02", "pan", topic: "food");
        });
        using var client = await LoginAsync(factory, "reviewer");
        int chipId;
        await using (var database = factory.OpenDatabase())
        {
            chipId = (await database.WordAlternatives.SingleAsync()).Id;
        }
        using (var empty = await PostAsync(client, "/api/review/actions", new { actions = Array.Empty<object>() }))
        {
            await AssertJsonErrorAsync(empty, HttpStatusCode.BadRequest, "bad_request");
        }
        using (var missing = await PostAsync(client, "/api/review/actions", new { actions = (object?)null }))
        {
            await AssertJsonErrorAsync(missing, HttpStatusCode.BadRequest, "bad_request");
        }
        using (var tooMany = await PostAsync(client, "/api/review/actions", new
        {
            actions = Enumerable.Range(0, 51).Select(_ => Action(Guid.NewGuid().ToString(), 1, "approve")).ToArray()
        }))
        {
            await AssertJsonErrorAsync(tooMany, HttpStatusCode.BadRequest, "bad_request");
        }

        using var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new object?[]
            {
                null!,
                new { clientReviewId = "not-a-guid", wordId = 1, wordVersion = 1, action = "approve" },
                Action(Guid.NewGuid().ToString(), 1, "banish"),
                Action(Guid.NewGuid().ToString(), 999, "approve"),
                Action(Guid.NewGuid().ToString(), 1, "approve", wordVersion: 5),
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve",
                    alternativeId = (int?)chipId },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve",
                    field = "gloss" },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_edit" },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "whole", senseOrder = (int?)null, after = "x" } } },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "gloss", senseOrder = 9, after = "x" } } },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "gloss", senseOrder = 0, after = "gloss of abuela" } } },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_alt",
                    alternativeId = (int?)9999, senseOrder = (int?)null },
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1, action = "approve_alt",
                    alternativeId = (int?)chipId, field = "gloss" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(13, results.Length);
            Assert.Equal(new[] { "invalid", "invalid", "invalid", "invalid", "stale_version", "invalid", "invalid",
                "invalid", "invalid", "invalid", "invalid", "invalid", "invalid" },
                results.Select(row => row.GetProperty("result").GetString()).ToArray());
            Assert.All(results, row => Assert.False(row.TryGetProperty("word", out _)));
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Empty(await database.WordReviews.ToListAsync());
            Assert.All(await database.Words.ToListAsync(), word => Assert.Equal("pending_review", word.Status));
            Assert.Equal(1, (await database.Words.SingleAsync(word => word.Id == 1)).Version);
        }

        // decision_ms is clamped, not rejected: an offline clock skew must not lose the verdict.
        using (var clamped = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new { clientReviewId = Guid.NewGuid().ToString(), wordId = 2, wordVersion = 1, action = "approve",
                    decisionMs = 9_999_999 }
            }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal(600_000, (await database.WordReviews.SingleAsync()).DecisionMs);
        }
    }

    [Fact]
    public async Task ApprovedListExposesTheCurrentVersionSoARevisedWordCanBeDemoted()
    {
        // An approved word edited after approval keeps version > 1. The approved list must return that
        // real version, because demote still enforces the version precondition like every action.
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved", topic: "family",
                glosses: ["grandmother"], statusChangedAt: Now.AddDays(-1).ToUnixTimeMilliseconds(),
                approvedVersion: 3, version: 3);
        });
        using var client = await LoginAsync(factory, "reviewer");

        int version;
        using (var body = await JsonAsync(await client.GetAsync("/api/review/approved", CancellationToken.None)))
        {
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(1, item.GetProperty("wordId").GetInt32());
            version = item.GetProperty("version").GetInt32();
            Assert.Equal(3, version);
        }

        // A stale version is still a stale_version result and leaves the word approved.
        using (var stale = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote", wordVersion: version - 1) }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            using var body = await JsonAsync(stale);
            Assert.Equal("stale_version", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal("approved", (await database.Words.AsNoTracking().SingleAsync()).Status);
        }

        // The version the approved list returned is exactly what demote requires.
        using (var applied = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "demote", wordVersion: version) }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            using var body = await JsonAsync(applied);
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("applied", result.GetProperty("result").GetString());
            Assert.Equal("pending_review", result.GetProperty("word").GetProperty("status").GetString());
            Assert.Equal(3, result.GetProperty("word").GetProperty("version").GetInt32());
        }
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.AsNoTracking().SingleAsync();
            Assert.Equal("pending_review", word.Status);
            Assert.Equal(3, word.Version);
        }
    }

    [Fact]
    public async Task ApprovedListSearchesKeysetPagesAndFallsBackForPreM4Rows()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            // Pre-M4 approval: status_changed_at/approved_version NULL, only the CLI revision exists.
            AddWord(database, 1, "es-abuela-01", "abuela", status: "approved", topic: "family",
                glosses: ["grandmother"]);
            database.WordRevisions.Add(new WordRevision
            {
                WordId = 1, Version = 1, Action = "approve", ActorUserId = null,
                SnapshotJson = Snapshot("es-abuela-01", "abuela", "family", ["grandmother"]).ToJsonString(),
                CreatedAt = Now.AddDays(-4).ToUnixTimeMilliseconds()
            });
            // M4 approval: status_changed_at present.
            AddWord(database, 2, "es-pan-02", "pan", status: "approved", topic: "food", glosses: ["bread"],
                statusChangedAt: Now.AddDays(-1).ToUnixTimeMilliseconds(), approvedVersion: 1);
            // No revision at all: updated_at is the last resort.
            AddWord(database, 3, "es-mesa-03", "mesa", status: "approved", topic: "household",
                glosses: ["table"]);
        });
        using var client = await LoginAsync(factory, "reviewer");

        using (var body = await JsonAsync(await client.GetAsync("/api/review/approved", CancellationToken.None)))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { 2, 3, 1 }, items.Select(item => item.GetProperty("wordId").GetInt32()).ToArray());
            Assert.Equal(Now.AddDays(-1), items[0].GetProperty("approvedAt").GetDateTimeOffset().ToUniversalTime());
            // The pre-M4 row reports the CLI approval instant, never the epoch.
            Assert.Equal(Now.AddDays(-4), items[2].GetProperty("approvedAt").GetDateTimeOffset().ToUniversalTime());
            Assert.Equal("bread", items[0].GetProperty("gloss").GetString());
            Assert.Equal("food", items[0].GetProperty("topic").GetString());
            Assert.Equal("pan", items[0].GetProperty("headword").GetString());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/review/approved?q=bread", CancellationToken.None)))
        {
            Assert.Equal(new[] { "es-pan-02" }, body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("key").GetString()).ToArray());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/review/approved?q=ABUEL", CancellationToken.None)))
        {
            Assert.Equal(new[] { "es-abuela-01" }, body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("key").GetString()).ToArray());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/review/approved?limit=1", CancellationToken.None)))
        {
            var first = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(2, first.GetProperty("wordId").GetInt32());
            using var next = await JsonAsync(await client.GetAsync("/api/review/approved?limit=1&after=2",
                CancellationToken.None));
            var second = Assert.Single(next.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(3, second.GetProperty("wordId").GetInt32());
        }
        foreach (var query in new[] { "?limit=0", "?limit=51", "?limit=x", "?after=0", "?after=x", "?after=99" })
        {
            using var rejected = await client.GetAsync("/api/review/approved" + query, CancellationToken.None);
            await AssertJsonErrorAsync(rejected, HttpStatusCode.BadRequest, "bad_request");
        }
    }

    [Fact]
    public async Task RejectedEditNeverLeaksPartialChangesIntoTheNextItem()
    {
        // A batch is one transaction: an item whose second edit is invalid must not leave the first
        // edit on the tracked entity for the next item's SaveChanges.
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", glosses: ["grandmother"]);
            AddWord(database, 2, "es-pan-02", "pan", topic: "food");
        });
        using var client = await LoginAsync(factory, "reviewer");
        using (var body = await JsonAsync(await PostAsync(client, "/api/review/actions", new
        {
            actions = new object[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = 1, wordVersion = 1,
                    action = "approve_edit", field = (string?)null, senseOrder = (int?)null,
                    alternativeId = (int?)null,
                    edits = new object[]
                    {
                        new { field = "lemma", senseOrder = (int?)null, after = "abuelita" },
                        new { field = "gloss", senseOrder = 0, after = "grandmother" }
                    },
                    note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)null
                },
                Action(Guid.NewGuid().ToString(), 2, "approve")
            }
        })))
        {
            Assert.Equal(new[] { "invalid", "applied" },
                body.RootElement.GetProperty("results").EnumerateArray()
                    .Select(row => row.GetProperty("result").GetString()).ToArray());
        }
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.AsNoTracking().SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal("abuela", word.Headword);
            Assert.Equal(1, word.Version);
            Assert.Equal("grandmother",
                (await database.Senses.AsNoTracking().SingleAsync(sense => sense.WordId == 1)).EnGloss);
            Assert.Equal(0, await database.WordRevisions.CountAsync(revision => revision.WordId == 1
                && revision.Action != "import"));
            Assert.Equal(1, await database.WordReviews.CountAsync());
        }
    }

    [Fact]
    public async Task ReviewRoutesAreReviewerOnlyAndWritesNeedTheCustomHeader()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
        });
        using var anonymous = factory.CreateHttpClient();
        using (var response = await anonymous.GetAsync("/api/review/queue", CancellationToken.None))
        {
            await AssertJsonErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }
        using (var response = await anonymous.GetAsync("/api/review/approved", CancellationToken.None))
        {
            await AssertJsonErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }

        using var owner = await LoginAsync(factory, "learner");
        foreach (var path in new[] { "/api/review/queue", "/api/review/approved" })
        {
            using var response = await owner.GetAsync(path, CancellationToken.None);
            await AssertJsonErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
        using (var response = await PostAsync(owner, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "approve") }
        }))
        {
            await AssertJsonErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }

        using var reviewer = await LoginAsync(factory, "reviewer");
        using (var response = await reviewer.GetAsync("/api/learn/words", CancellationToken.None))
        {
            await AssertJsonErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
        using (var headerless = new HttpRequestMessage(HttpMethod.Post, "/api/review/actions")
               {
                   Content = JsonContent.Create(new { actions = Array.Empty<object>() })
               })
        using (var response = await reviewer.SendAsync(headerless, CancellationToken.None))
        {
            await AssertJsonErrorAsync(response, HttpStatusCode.BadRequest, "missing_x_palabras");
        }
    }

    [Fact]
    public async Task CliApprovalStaysLenientWhileTheReviewerApiIsStrict()
    {
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            // A draft is still outside the reviewer's strict matrix, while the CLI keeps its
            // shipped "approve from any status" semantics.
            AddWord(database, 1, "es-abuela-01", "abuela", status: "draft");
        });
        using var client = await LoginAsync(factory, "reviewer");
        using (var response = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), 1, "approve") }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("invalid_transition", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        await using (var database = factory.OpenDatabase())
        {
            Assert.Equal(new ContentApprovalResult(1, 0),
                await ContentApproval.ApproveAsync(database, ["es-abuela-01"], false, CancellationToken.None,
                    factory.Time));
            var word = await database.Words.AsNoTracking().SingleAsync();
            Assert.Equal("approved", word.Status);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), word.StatusChangedAt);
            Assert.Equal(1, word.ApprovedVersion);
        }
    }

    // ---- request builders -------------------------------------------------

    private static object Action(string clientReviewId, int wordId, string action, int wordVersion = 1,
        string? note = null) => new
    {
        clientReviewId, wordId, wordVersion, action, field = (string?)null, senseOrder = (int?)null,
        alternativeId = (int?)null, edits = (object?)null, note, decisionMs = (int?)null,
        viaBatch = (bool?)null
    };

    private static object AltAction(string clientReviewId, int wordId, int chipId, int wordVersion) => new
    {
        clientReviewId, wordId, wordVersion, action = "approve_alt", field = (string?)null,
        senseOrder = (int?)null, alternativeId = (int?)chipId, edits = (object?)null, note = (string?)null,
        decisionMs = (int?)null, viaBatch = (bool?)null
    };

    private static object EditAction(string clientReviewId, int wordId, string after) => new
    {
        clientReviewId, wordId, wordVersion = 1, action = "approve_edit", field = (string?)null,
        senseOrder = (int?)null, alternativeId = (int?)null,
        edits = new object[] { new { field = "lemma", senseOrder = (int?)null, after } },
        note = (string?)null, decisionMs = (int?)null, viaBatch = (bool?)null
    };

    private static object BatchAction(string clientReviewId, int wordId) => new
    {
        clientReviewId, wordId, wordVersion = 1, action = "approve", field = (string?)null,
        senseOrder = (int?)null, alternativeId = (int?)null, edits = (object?)null, note = (string?)null,
        decisionMs = (int?)null, viaBatch = (bool?)true
    };

    // ---- seeding ----------------------------------------------------------

    private static void AddWord(PalabrasDbContext database, int id, string key, string headword,
        string status = "pending_review", string topic = "family", double? agreement = null, string? band = null,
        string? lint = null, string? article = null, string? gender = null, string pos = "noun",
        IReadOnlyList<string>? glosses = null, IReadOnlyList<(string Kind, string Value, string Reason)>? alternativeValues = null,
        long? statusChangedAt = null, int? approvedVersion = null, int reducedConfidence = 0, int version = 1)
    {
        var senseGlosses = glosses ?? ["gloss of " + headword];
        database.Words.Add(new Word
        {
            Id = id, Key = key, Headword = headword, Article = article, Gender = gender, Pos = pos, Cefr = "A1",
            RegionScope = "ES", Register = "neutral", Status = status, Version = version,
            ContentHash = "hash-" + key, ProvenanceMethod = "hand-authored",
            CouncilAgreement = agreement, CouncilBand = band, LintWarnings = lint,
            ReducedConfidence = reducedConfidence, StatusChangedAt = statusChangedAt,
            ApprovedVersion = approvedVersion, CreatedAt = Now.AddDays(-10).ToUnixTimeMilliseconds(),
            UpdatedAt = Now.AddDays(-2).ToUnixTimeMilliseconds()
        });
        database.WordTopics.Add(new WordTopic { WordId = id, Topic = topic });
        for (var order = 0; order < senseGlosses.Count; order++)
        {
            database.Senses.Add(new Sense
            {
                Id = id * 10 + order, WordId = id, SenseOrder = order, EnGloss = senseGlosses[order],
                EnAccepted = JsonSerializer.Serialize(new[] { senseGlosses[order] }),
                EsAccepted = JsonSerializer.Serialize(new[] { headword }),
                ExampleEs = "Ejemplo de " + headword + ".", ExampleEn = "Example of " + headword + "."
            });
        }
        var snapshot = Snapshot(key, headword, topic, senseGlosses, article, gender, pos, status);
        database.WordRevisions.Add(new WordRevision
        {
            WordId = id, Version = version, Action = "import", SnapshotJson = snapshot.ToJsonString(),
            CreatedAt = Now.AddDays(-10).ToUnixTimeMilliseconds()
        });
        foreach (var (kind, value, reason) in alternativeValues ?? [])
        {
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = id, Kind = kind, Value = value, Reason = reason,
                Rank = database.WordAlternatives.Local.Count(alternative => alternative.WordId == id)
            });
        }
    }

    private static JsonObject Snapshot(string key, string headword, string topic,
        IReadOnlyList<string> glosses, string? article = null, string? gender = null, string pos = "noun",
        string status = "pending_review")
    {
        var senses = new JsonArray();
        foreach (var gloss in glosses)
        {
            senses.Add(new JsonObject
            {
                ["gloss"] = gloss,
                ["example"] = "Ejemplo de " + headword + ".",
                ["exampleGloss"] = "Example of " + headword + ".",
                ["acceptedEs"] = new JsonArray(headword),
                ["acceptedEn"] = new JsonArray(gloss)
            });
        }
        return new JsonObject
        {
            ["id"] = key,
            ["headword"] = headword,
            ["article"] = article,
            ["gender"] = gender,
            ["partOfSpeech"] = pos,
            ["cefr"] = "A1",
            ["region"] = new JsonObject { ["scope"] = "ES", ["notes"] = (string?)null },
            ["register"] = "neutral",
            ["topics"] = new JsonArray(topic),
            ["senses"] = senses,
            ["alternatives"] = new JsonArray(),
            ["provenance"] = new JsonObject { ["method"] = "hand-authored" },
            ["status"] = status,
            ["updatedAt"] = Now.AddDays(-2).ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static void AddCard(PalabrasDbContext database, int senseId, string direction = "es_en",
        CardState state = CardState.New, int epoch = 0, int reps = 0, int lapses = 0, double? stability = null,
        double? difficulty = null, long? dueMs = null, long? lastReviewMs = null)
    {
        database.Cards.Add(new Card
        {
            UserId = 1, SenseId = senseId, Direction = direction, State = state, Epoch = epoch, Reps = reps,
            Lapses = lapses, Stability = stability, Difficulty = difficulty,
            DueMs = dueMs ?? Now.ToUnixTimeMilliseconds(), LastReviewMs = lastReviewMs,
            CreatedAt = Now.AddDays(-5).ToUnixTimeMilliseconds()
        });
    }

    // ---- HTTP helpers -----------------------------------------------------

    private static async Task<HttpClient> LoginAsync(ReviewApplicationFactory factory, string username)
    {
        var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username, password = Password })
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Palabras", "1");
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

    // Walks `status=all` from the first page until `nextAfter` is absent, returning every page in
    // order. The page cap turns a cursor that never clears into a test failure instead of a hang.
    private static async Task<List<(int WordId, string Status)[]>> WalkAllScopeAsync(HttpClient client, int limit)
    {
        var pages = new List<(int WordId, string Status)[]>();
        string? cursor = null;
        do
        {
            var path = "/api/review/queue?status=all&limit=" + limit.ToString(CultureInfo.InvariantCulture)
                + (cursor is null ? string.Empty : "&after=" + cursor);
            using var body = await JsonAsync(await client.GetAsync(path, CancellationToken.None));
            pages.Add(body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => (item.GetProperty("wordId").GetInt32(),
                    item.GetProperty("status").GetString()!))
                .ToArray());
            Assert.True(pages.Count < 20, "status=all walk did not terminate");
            cursor = body.RootElement.TryGetProperty("nextAfter", out var next)
                ? next.GetInt32().ToString(CultureInfo.InvariantCulture)
                : null;
        }
        while (cursor is not null);
        return pages;
    }

    private static async Task AssertJsonErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(error, body.RootElement.GetProperty("error").GetString());
    }

    private sealed class ReviewApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash(Password);
        // Task rule: DB-backed tests use throwaway files under /tmp/palabras-m4, never /data.
        private readonly string _root = Path.Combine("/tmp", "palabras-m4", Guid.NewGuid().ToString("N"));
        private readonly FakeTimeProvider _time;

        internal ReviewApplicationFactory(DateTimeOffset now, Action<PalabrasDbContext>? seed = null)
        {
            _time = new FakeTimeProvider(now);
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var database = new DatabaseSettings(DatabasePath).CreateContext();
            database.Database.Migrate();
            var stamp = DateTimeOffset.UnixEpoch;
            database.Users.AddRange(
                new User { Id = 1, Username = "learner", Role = "owner", PasswordHash = PasswordHash, CreatedAt = stamp, UpdatedAt = stamp },
                new User { Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = PasswordHash, CreatedAt = stamp, UpdatedAt = stamp });
            database.SaveChanges();
            seed?.Invoke(database);
            database.SaveChanges();
        }

        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

        internal string ImportDirectory => Path.Combine(_root, "source");

        internal FakeTimeProvider Time => _time;

        internal PalabrasDbContext OpenDatabase() => new DatabaseSettings(DatabasePath).CreateContext();

        internal HttpClient CreateHttpClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://palabras.siebentower.nl"), AllowAutoRedirect = false, HandleCookies = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_time);
            });
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
