using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Accounts;
using Palabras.Learn;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

// M6.6 stats v1 contract tests (design .opencode/runs/T-20261018-0332/04-stats-design.md §1/§3).
// Rows are hand-built so every count is checkable by eye, and every call goes through the real
// cookie auth + X-Palabras middleware against a migrated temp SQLite database.
public sealed class M66StatsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T09:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly LearnerClock Madrid = new(new FakeTimeProvider(Now), "Europe/Madrid");

    [Fact]
    public async Task StatsDefaultsToThirtyLocalDaysAndStaysWellFormedWithNoHistory()
    {
        using var factory = new StatsApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory, "learner");

        using var response = await client.GetAsync("/api/learn/stats", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var root = body.RootElement;

        Assert.Equal("2026-10-18", root.GetProperty("localDate").GetString());
        Assert.Equal("2026-09-19", root.GetProperty("from").GetString());
        Assert.Equal("2026-10-18", root.GetProperty("to").GetString());

        var totals = root.GetProperty("totals");
        foreach (var name in new[]
                 {
                     "answers", "hits", "misses", "formSlips", "overridden", "firstTry", "secondLook", "answerMs",
                     "activeDays", "newWords"
                 })
        {
            Assert.Equal(0.0, totals.GetProperty(name).GetDouble());
        }

        // Dense across the whole default range, zeros included, in date order.
        var days = root.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(30, days.Length);
        Assert.Equal("2026-09-19", days[0].GetProperty("date").GetString());
        Assert.Equal("2026-10-18", days[^1].GetProperty("date").GetString());
        Assert.All(days, day => Assert.Equal(0.0, day.GetProperty("answers").GetDouble()));

        Assert.Empty(root.GetProperty("byMode").EnumerateArray());
        Assert.Empty(root.GetProperty("hardest").EnumerateArray());
        Assert.Equal(0, root.GetProperty("quality").GetProperty("lateRows").GetInt32());
        Assert.Equal(0, root.GetProperty("quality").GetProperty("missingDurationRows").GetInt32());

        var retention = root.GetProperty("retention");
        Assert.Equal(0.90, retention.GetProperty("target").GetDouble());
        Assert.Equal(0, retention.GetProperty("all").GetProperty("reviews").GetInt32());
        Assert.Equal(0, retention.GetProperty("esEn").GetProperty("passed").GetInt32());
        Assert.Equal(0, retention.GetProperty("enEs").GetProperty("reviews").GetInt32());
        // The response carries counts only: a percentage is the UI's decision, never serialized.
        Assert.False(retention.GetProperty("all").TryGetProperty("percent", out _));

        var words = root.GetProperty("words");
        Assert.Equal(0, words.GetProperty("approved").GetInt32());
        foreach (var stage in new[] { "nuevo", "aprendiendo", "reciente", "consolidada", "dominada" })
        {
            Assert.Equal(0, words.GetProperty("stages").GetProperty(stage).GetInt32());
        }
        Assert.Equal(0, words.GetProperty("leechCards").GetInt32());
        Assert.Equal(0, words.GetProperty("suspendedCards").GetProperty("manual").GetInt32());
        Assert.Equal(0, words.GetProperty("suspendedCards").GetProperty("leech").GetInt32());
    }

    [Fact]
    public async Task RangeBoundariesAreInclusiveLocalDayEdges()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            AddLog(database, 1, 1, Rating.Good, StartOf(2026, 10, 10));
            AddLog(database, 1, 1, Rating.Good, EndOf(2026, 10, 10));
            AddLog(database, 1, 1, Rating.Good, StartOf(2026, 10, 11));
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using (var body = await JsonAsync(await client.GetAsync(
                   "/api/learn/stats?from=2026-10-10&to=2026-10-10", CancellationToken.None)))
        {
            var root = body.RootElement;
            // Both the first millisecond and the last millisecond of the local day are inside.
            Assert.Equal(2, root.GetProperty("totals").GetProperty("answers").GetInt32());
            var days = root.GetProperty("days").EnumerateArray().ToArray();
            Assert.Single(days);
            Assert.Equal("2026-10-10", days[0].GetProperty("date").GetString());
            Assert.Equal(2, days[0].GetProperty("answers").GetInt32());
        }

        using (var body = await JsonAsync(await client.GetAsync(
                   "/api/learn/stats?from=2026-10-11&to=2026-10-11", CancellationToken.None)))
        {
            Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("answers").GetInt32());
        }
    }

    [Theory]
    [InlineData("?from=2026-10-18&to=2026-10-01")]   // inverted
    [InlineData("?from=2025-01-01&to=2026-10-18")]   // 656 days
    [InlineData("?from=2025-10-17&to=2026-10-18")]   // 367 days, one over the limit
    [InlineData("?to=2026-10-19")]                   // after local today
    [InlineData("?from=2026-13-01")]                 // impossible month
    [InlineData("?from=2026-1-1")]                   // wrong format
    [InlineData("?to=not-a-date")]
    [InlineData("?from=2026-10-18&to=2026-10-19")]
    public async Task InvalidRangesAnswerBadRequest(string query)
    {
        using var factory = new StatsApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory, "learner");
        using var response = await client.GetAsync("/api/learn/stats" + query, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task ThreeHundredSixtySixDayRangeIsAccepted()
    {
        using var factory = new StatsApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory, "learner");
        using var response = await client.GetAsync("/api/learn/stats?from=2025-10-18&to=2026-10-18",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal(366, body.RootElement.GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task HitsMissesFormSlipsAndOverridesAreCountedSeparately()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            var at = LocalNoon(2026, 10, 18);
            // 1: applied but the card was still learning -> hit, not a retention review.
            AddLog(database, 1, 1, Rating.Good, at, stateBefore: CardState.Learning);
            // 2: Again is the only miss.
            AddLog(database, 1, 1, Rating.Again, at);
            // 3: Hard with a form slip -> hit and form slip.
            AddLog(database, 1, 1, Rating.Hard, at, mode: "typed", isCorrectForm: false);
            // 4: self-override -> hit, and reported separately.
            AddLog(database, 1, 1, Rating.Good, at, mode: "typed", isCorrectForm: true, overridden: true);
            // 5: duration missing -> no answer time contributed.
            AddLog(database, 1, 1, Rating.Good, at, mode: "cloze", isCorrectForm: true, durationMs: null);
            // 6: rejected (never applied) still counts as an answer the learner gave.
            AddLog(database, 1, 1, Rating.Again, at, countsForSrs: false);
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var root = body.RootElement;
        var totals = root.GetProperty("totals");
        Assert.Equal(6, totals.GetProperty("answers").GetInt32());
        Assert.Equal(4, totals.GetProperty("hits").GetInt32());
        Assert.Equal(2, totals.GetProperty("misses").GetInt32());
        Assert.Equal(1, totals.GetProperty("formSlips").GetInt32());
        Assert.Equal(1, totals.GetProperty("overridden").GetInt32());
        Assert.Equal(4000L, totals.GetProperty("answerMs").GetInt64());

        // Modes present only, ordered by the stored string (a future mode needs no stats change).
        var modes = root.GetProperty("byMode").EnumerateArray().ToArray();
        Assert.Equal(new[] { "cloze", "recognition", "typed" },
            modes.Select(mode => mode.GetProperty("mode").GetString()).ToArray());
        Assert.Equal(new[] { 1, 3, 2 }, modes.Select(mode => mode.GetProperty("answers").GetInt32()).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, modes.Select(mode => mode.GetProperty("hits").GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 2, 0 }, modes.Select(mode => mode.GetProperty("misses").GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, modes.Select(mode => mode.GetProperty("formSlips").GetInt32()).ToArray());

        // Retention: applied mature reviews only. Row 1 is not mature, row 6 never applied.
        var retention = root.GetProperty("retention");
        Assert.Equal(4, retention.GetProperty("all").GetProperty("reviews").GetInt32());
        Assert.Equal(3, retention.GetProperty("all").GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("quality").GetProperty("missingDurationRows").GetInt32());

        var today = root.GetProperty("days").EnumerateArray().Single(day =>
            day.GetProperty("date").GetString() == "2026-10-18");
        Assert.Equal(6, today.GetProperty("answers").GetInt32());
        Assert.Equal(4, today.GetProperty("hits").GetInt32());
        Assert.Equal(2, today.GetProperty("misses").GetInt32());
        Assert.Equal(4000L, today.GetProperty("answerMs").GetInt64());
    }

    [Fact]
    public async Task FirstTryClassifiesTheFirstRowOfEachSessionAndCardGroup()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            AddCard(database, 2, 1, direction: "en_es", state: CardState.Review);
            var at = LocalNoon(2026, 10, 18);
            AddLog(database, 1, 1, Rating.Good, at, sessionId: "s1");
            AddLog(database, 2, 1, Rating.Again, at.AddMinutes(1), sessionId: "s1");   // first row of its group
            AddLog(database, 1, 1, Rating.Good, at.AddMinutes(2), sessionId: "s1");    // requeue of card 1
            AddLog(database, 2, 1, Rating.Good, at.AddMinutes(3), sessionId: "s1");    // repeat of card 2
            // A NULL session id cannot be paired with anything, so each row is its own group and
            // its own first row.
            AddLog(database, 1, 1, Rating.Good, at.AddMinutes(4), sessionId: null);
            AddLog(database, 1, 1, Rating.Good, at.AddMinutes(5), sessionId: null);
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var totals = body.RootElement.GetProperty("totals");
        Assert.Equal(6, totals.GetProperty("answers").GetInt32());
        // sessionTotals semantics: s1/card1 first row Good -> right first try; s1/card2 first row
        // Again -> second look (its later Good is a repeat, not a rescue of the headline); the two
        // NULL-session rows are each their own group and each a right first try.
        Assert.Equal(3, totals.GetProperty("firstTry").GetInt32());
        Assert.Equal(1, totals.GetProperty("secondLook").GetInt32());
        // The headline is card-level: firstTry + secondLook is the group count, not `answers`.
        Assert.NotEqual(totals.GetProperty("answers").GetInt32(),
            totals.GetProperty("firstTry").GetInt32() + totals.GetProperty("secondLook").GetInt32());
    }

    [Fact]
    public async Task AFirstAgainFollowedByAHitIsASecondLookAndANullSessionGroupIsItsOwnFirstRow()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            AddCard(database, 2, 1, direction: "en_es", state: CardState.Review);
            var at = LocalNoon(2026, 10, 18);
            // Mutation pin: counting answers or groups instead of classifying the first row makes
            // this a hit. The later Good must not rescue the group.
            AddLog(database, 1, 1, Rating.Again, at, sessionId: "s1");
            AddLog(database, 1, 1, Rating.Good, at.AddMinutes(1), sessionId: "s1");
            // Out-of-order delivery: the smallest reviewed_at_ms wins even though it is stored later.
            AddLog(database, 2, 1, Rating.Again, at.AddMinutes(3), sessionId: "s2");
            AddLog(database, 2, 1, Rating.Easy, at.AddMinutes(2), sessionId: "s2");
            // A NULL session is a group of one: a lone Again is a second look, not a first try.
            AddLog(database, 2, 1, Rating.Again, at.AddMinutes(4), sessionId: null);
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var totals = body.RootElement.GetProperty("totals");
        Assert.Equal(5, totals.GetProperty("answers").GetInt32());
        Assert.Equal(1, totals.GetProperty("firstTry").GetInt32());    // only s2/card2 (Easy first)
        Assert.Equal(2, totals.GetProperty("secondLook").GetInt32());  // s1/card1 (Again) and the NULL row
    }

    [Fact]
    public async Task NewWordsCountEsEnIntroductionsOnly()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            // One approved word per card keeps the unique (sense, user, direction) index happy.
            for (var id = 1; id <= 5; id++)
            {
                AddWord(database, id, $"es-word-{id:00}", $"word{id}");
            }
            AddCard(database, 1, 1, introduced: "2026-10-10");
            // An en_es unlock is the same word in the other direction: never a new word (O-5).
            AddCard(database, 2, 2, direction: "en_es", introduced: "2026-10-10");
            AddCard(database, 3, 3, introduced: "2026-10-12");
            AddCard(database, 4, 4, introduced: "2026-09-01");   // before the range
            AddCard(database, 5, 5, introduced: null);           // never introduced
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats?from=2026-10-09&to=2026-10-12",
            CancellationToken.None));
        var root = body.RootElement;
        Assert.Equal(2, root.GetProperty("totals").GetProperty("newWords").GetInt32());
        var days = root.GetProperty("days").EnumerateArray()
            .ToDictionary(day => day.GetProperty("date").GetString()!, day => day.GetProperty("newWords").GetInt32());
        Assert.Equal(4, days.Count);
        Assert.Equal(1, days["2026-10-10"]);
        Assert.Equal(0, days["2026-10-11"]);
        Assert.Equal(1, days["2026-10-12"]);
        Assert.Equal(0, days["2026-10-09"]);
        // Introductions are not answers.
        Assert.Equal(0, root.GetProperty("totals").GetProperty("activeDays").GetInt32());
    }

    [Fact]
    public async Task WordStagesLeechesAndPausesUseApprovedActiveWordsOnly()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            for (var id = 1; id <= 7; id++)
            {
                AddWord(database, id, $"es-word-{id:00}", $"word{id}",
                    status: id == 7 ? "pending_review" : "approved");
            }
            // A retained sense the content file dropped: sense_order 1 is outside the snapshot.
            AddSense(database, 61, 6, 1, enGloss: "dropped gloss");

            AddCard(database, 1, 1, suspended: "word_not_approved");                 // still `nuevo`
            AddCard(database, 2, 2, state: CardState.Learning, suspended: "manual"); // aprendizaje + pause
            AddCard(database, 3, 3, state: CardState.Review, stability: 5);          // reciente
            AddCard(database, 4, 4, state: CardState.Review, stability: 30);         // consolidada
            AddCard(database, 5, 5, state: CardState.Review, stability: 100);        // dominada
            AddCard(database, 6, 6, state: CardState.Review, stability: 30, leech: true, suspended: "leech");
            // A strong card on the removed sense must not promote word 6 (active-sense gate).
            AddCard(database, 7, 61, state: CardState.Review, stability: 95);
            // Pending word: a card and a review exist, but nothing here may surface.
            AddCard(database, 8, 7, state: CardState.Review, stability: 95, leech: true);
            AddLog(database, 8, 7, Rating.Again, LocalNoon(2026, 10, 18));
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var words = body.RootElement.GetProperty("words");
        Assert.Equal(6, words.GetProperty("approved").GetInt32());
        var stages = words.GetProperty("stages");
        Assert.Equal(1, stages.GetProperty("nuevo").GetInt32());
        Assert.Equal(1, stages.GetProperty("aprendiendo").GetInt32());
        Assert.Equal(1, stages.GetProperty("reciente").GetInt32());
        Assert.Equal(2, stages.GetProperty("consolidada").GetInt32());
        Assert.Equal(1, stages.GetProperty("dominada").GetInt32());
        // The leech card on the pending word is invisible; the approved one is counted.
        Assert.Equal(1, words.GetProperty("leechCards").GetInt32());
        Assert.Equal(1, words.GetProperty("suspendedCards").GetProperty("manual").GetInt32());
        Assert.Equal(1, words.GetProperty("suspendedCards").GetProperty("leech").GetInt32());
        // The pending word's miss stays inside totals but is never named.
        Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("misses").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("hardest").EnumerateArray());
    }

    [Fact]
    public async Task RetentionSplitsByDirectionExcludesRejectedAndUsesTheStoredTarget()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-coger-01", "coger");
            AddWord(database, 3, "es-perro-01", "perro");
            AddWord(database, 4, "es-gato-01", "gato");
            AddCard(database, 1, 1, state: CardState.Review);
            AddCard(database, 2, 2, direction: "en_es", state: CardState.Review);
            AddCard(database, 3, 3, state: CardState.Review);
            AddCard(database, 4, 4, direction: "en_es", state: CardState.Review);
            database.UserSettings.Add(new UserSettings { UserId = 1, DesiredRetention = 0.85 });
            var at = LocalNoon(2026, 10, 18);
            AddLog(database, 1, 1, Rating.Good, at);
            AddLog(database, 1, 1, Rating.Again, at);
            AddLog(database, 2, 2, Rating.Good, at, mode: "typed", isCorrectForm: true);
            AddLog(database, 2, 2, Rating.Easy, at, mode: "typed", isCorrectForm: true);
            AddLog(database, 3, 3, Rating.Good, at, stateBefore: CardState.Learning);
            AddLog(database, 4, 4, Rating.Again, at, mode: "typed", countsForSrs: false);
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var retention = body.RootElement.GetProperty("retention");
        Assert.Equal(0.85, retention.GetProperty("target").GetDouble());
        Assert.Equal(4, retention.GetProperty("all").GetProperty("reviews").GetInt32());
        Assert.Equal(3, retention.GetProperty("all").GetProperty("passed").GetInt32());
        // Self-graded recognition vs checked typed answers are never merged into one number.
        Assert.Equal(2, retention.GetProperty("esEn").GetProperty("reviews").GetInt32());
        Assert.Equal(1, retention.GetProperty("esEn").GetProperty("passed").GetInt32());
        Assert.Equal(2, retention.GetProperty("enEs").GetProperty("reviews").GetInt32());
        Assert.Equal(2, retention.GetProperty("enEs").GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task HardestListsFiveApprovedWordsByMissesAndNeverNamesAPendingWord()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            var headwords = new[] { "auno", "bdo", "ctres", "dcuatro", "euno", "funo", "gsin" };
            var misses = new[] { 3, 2, 2, 1, 1, 1, 0 };
            for (var id = 1; id <= 7; id++)
            {
                AddWord(database, id, $"es-word-{id:00}", headwords[id - 1], article: "el",
                    enGloss: $"gloss-{id}");
                AddCard(database, id, id, state: CardState.Review);
            }
            AddWord(database, 8, "es-zzz-01", "zzz", status: "pending_review");
            AddCard(database, 8, 8, state: CardState.Review);
            var at = LocalNoon(2026, 10, 18);
            for (var id = 1; id <= 7; id++)
            {
                for (var hit = 0; hit < misses[id - 1]; hit++)
                {
                    AddLog(database, id, id, Rating.Again, at);
                }
            }
            for (var miss = 0; miss < 5; miss++)
            {
                AddLog(database, 8, 8, Rating.Again, at);
            }
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var hardest = body.RootElement.GetProperty("hardest").EnumerateArray().ToArray();
        Assert.Equal(5, hardest.Length);
        Assert.Equal(new[] { "es-word-01", "es-word-02", "es-word-03", "es-word-04", "es-word-05" },
            hardest.Select(item => item.GetProperty("key").GetString()).ToArray());
        Assert.Equal(new[] { 3, 2, 2, 1, 1 }, hardest.Select(item => item.GetProperty("misses").GetInt32()).ToArray());
        Assert.Equal("el", hardest[0].GetProperty("article").GetString());
        Assert.Equal("gloss-1", hardest[0].GetProperty("gloss").GetString());
        Assert.DoesNotContain(hardest, item => item.GetProperty("key").GetString() == "es-zzz-01");
    }

    [Fact]
    public async Task LateFlushesAndMissingDurationsAreSurfacedAsQualityNotAsFacts()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            var reviewed = LocalNoon(2026, 10, 17);
            // Flushed 25 h later: it hit the [now-24h, now+5min] clamp, so its day is suspect.
            AddLog(database, 1, 1, Rating.Good, reviewed, receivedAt: reviewed.AddHours(25));
            // 23.5 h is the threshold pin: the operational test is > 23 h, NOT > 24 h. A clamped
            // row has received - reviewed = 24 h - processing time, i.e. just under 24 h, so the
            // 23 h margin is the only way to catch it. This row must be flagged by 23 h and would
            // be missed by a 24 h threshold, so changing the constant fails this assertion.
            AddLog(database, 1, 1, Rating.Good, reviewed, receivedAt: reviewed.AddMinutes(23 * 60 + 30));
            // On time: not late, and the only row without a duration.
            AddLog(database, 1, 1, Rating.Good, reviewed, receivedAt: reviewed.AddHours(2), durationMs: null);
        });
        using var client = await LearnerClientAsync(factory, "learner");

        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        var quality = body.RootElement.GetProperty("quality");
        Assert.Equal(2, quality.GetProperty("lateRows").GetInt32());
        Assert.Equal(1, quality.GetProperty("missingDurationRows").GetInt32());
        Assert.Equal(2 * 800L, body.RootElement.GetProperty("totals").GetProperty("answerMs").GetInt64());
    }

    [Fact]
    public async Task StatsRejectsAnonymousAndReviewerCallers()
    {
        using var factory = new StatsApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        using var unauthorized = await anonymous.GetAsync("/api/learn/stats", CancellationToken.None);
        await AssertJsonError(unauthorized, HttpStatusCode.Unauthorized, "unauthorized");

        using var reviewer = await LearnerClientAsync(factory, "reviewer");
        using var forbidden = await reviewer.GetAsync("/api/learn/stats", CancellationToken.None);
        await AssertJsonError(forbidden, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task AnotherLearnersRowsNeverAppear()
    {
        // The schema permits exactly one owner and one reviewer (unique role, PalabrasDbContext.cs:32-33),
        // so the second learner's rows are seeded under user 2 and read back through user 1's cookie.
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
            AddCard(database, 2, 1, userId: 2, state: CardState.Review, leech: true, suspended: "leech");
            var at = LocalNoon(2026, 10, 18);
            AddLog(database, 1, 1, Rating.Good, at, userId: 1);
            AddLog(database, 2, 1, Rating.Again, at, userId: 2);
            AddLog(database, 2, 1, Rating.Again, at, userId: 2);
        });

        using var mine = await LearnerClientAsync(factory, "learner");
        using var body = await JsonAsync(await mine.GetAsync("/api/learn/stats", CancellationToken.None));
        var root = body.RootElement;
        Assert.Equal(1, root.GetProperty("totals").GetProperty("answers").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("hits").GetInt32());
        Assert.Equal(0, root.GetProperty("totals").GetProperty("misses").GetInt32());
        Assert.Equal(0, root.GetProperty("words").GetProperty("leechCards").GetInt32());
        Assert.Empty(root.GetProperty("hardest").EnumerateArray());
        Assert.Equal(0, root.GetProperty("quality").GetProperty("missingDurationRows").GetInt32());

        // The rows do exist, so the emptiness above is the user filter and not an empty seed.
        await using var database = factory.OpenDatabase();
        Assert.Equal(3, await database.ReviewLogs.CountAsync());
    }

    [Fact]
    public async Task StatsQueriesRunOnSqliteWithoutClientEvaluation()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", enGloss: "grandmother");
            AddCard(database, 1, 1, state: CardState.Review);
            var at = LocalNoon(2026, 10, 18);
            AddLog(database, 1, 1, Rating.Good, at);
            AddLog(database, 1, 1, Rating.Again, at);
        });
        var clock = new LearnerClock(factory.Time, "Europe/Madrid");
        await using var database = factory.OpenDatabase();
        // EF Core throws for a predicate it cannot translate, so a pass here proves the range
        // filter, the string date bounds, the active-sense IN list and the first-row NOT EXISTS
        // subquery all stayed in SQL. The projections are scalar property reads plus one bool
        // comparison, so there is nothing left that could fall back to client evaluation. The
        // grouping afterwards is the documented in-memory aggregator (design §3).
        var input = await LearnStats.LoadAsync(database, 1, clock, Today.AddDays(-29), Today,
            CancellationToken.None);
        Assert.Equal(2, input.Rows.Count);
        Assert.Single(input.Words);
        Assert.Equal("grandmother", input.Words[0].Gloss);
        Assert.Equal("es_en", input.Directions[1]);
        Assert.Single(input.Cards);
        // Two rows in one (session, card) group: only the first-rated one comes back.
        Assert.Equal(Rating.Good, Assert.Single(input.FirstRowRatings));
    }

    [Fact]
    public void DaysStayDenseOneBucketPerLocalDateAcrossBothDstChanges()
    {
        // Autumn: 2026-10-25 has 25 h in Europe/Madrid. A row at local noon on each date must give
        // exactly one bucket per date, in order, with no duplicate or skipped date.
        var autumnClock = new LearnerClock(
            new FakeTimeProvider(DateTimeOffset.Parse("2026-10-28T12:00:00Z", CultureInfo.InvariantCulture)),
            "Europe/Madrid");
        var autumnRows = Enumerable.Range(0, 5)
            .Select(offset => Row(autumnClock, new DateOnly(2026, 10, 23).AddDays(offset)))
            .ToArray();
        var autumn = LearnStats.Aggregate(Input(autumnRows), autumnClock,
            new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 27));
        Assert.Equal(new[] { "2026-10-23", "2026-10-24", "2026-10-25", "2026-10-26", "2026-10-27" },
            autumn.Days.Select(day => day.Date).ToArray());
        Assert.All(autumn.Days, day => Assert.Equal(1, day.Answers));
        Assert.Equal(5, autumn.Totals.Answers);
        Assert.Equal(5, autumn.Totals.ActiveDays);

        // Spring: 2027-03-28 loses an hour.
        var springClock = new LearnerClock(
            new FakeTimeProvider(DateTimeOffset.Parse("2027-03-31T12:00:00Z", CultureInfo.InvariantCulture)),
            "Europe/Madrid");
        var springRows = Enumerable.Range(0, 5)
            .Select(offset => Row(springClock, new DateOnly(2027, 3, 26).AddDays(offset)))
            .ToArray();
        var spring = LearnStats.Aggregate(Input(springRows), springClock,
            new DateOnly(2027, 3, 26), new DateOnly(2027, 3, 30));
        Assert.Equal(new[] { "2027-03-26", "2027-03-27", "2027-03-28", "2027-03-29", "2027-03-30" },
            spring.Days.Select(day => day.Date).ToArray());
        Assert.All(spring.Days, day => Assert.Equal(1, day.Answers));
    }

    [Fact]
    public void AggregatorPassesAnUnknownFutureModeThroughByName()
    {
        var rows = new[]
        {
            Row(Madrid, new DateOnly(2026, 10, 18)) with { Mode = "listening", Rating = Rating.Again },
            Row(Madrid, new DateOnly(2026, 10, 18)) with { Mode = "choice" }
        };
        var stats = LearnStats.Aggregate(Input(rows), Madrid, new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 18));
        // Grouped by the stored string, never validated against an enum: M6.5's `choice` and any
        // later mode need no stats change.
        Assert.Equal(new[] { "choice", "listening" }, stats.ByMode.Select(mode => mode.Mode).ToArray());
        Assert.Equal(new[] { 1, 1 }, stats.ByMode.Select(mode => mode.Answers).ToArray());
        Assert.Equal(new[] { 0, 1 }, stats.ByMode.Select(mode => mode.Misses).ToArray());
    }

    [Fact]
    public async Task FiftyThousandRowsStillAnswerInsideTheSmokeBound()
    {
        using var factory = new StatsApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review);
        });
        var start = StartOf(2026, 10, 16).ToUnixTimeMilliseconds();
        await using (var database = factory.OpenDatabase())
        {
            // One statement instead of 50 000 tracked entities: the test is about the read path.
            database.Database.ExecuteSqlRaw(
                "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 50000) " +
                "INSERT INTO review_log (client_attempt_id, user_id, word_id, card_id, card_epoch, session_id, " +
                "rating, reviewed_at_ms, received_at_ms, elapsed_days, interval_after_days, duration_ms, mode, " +
                "overridden, state_before, state_after, due_after_ms, weights_version, counts_for_srs) " +
                "SELECT 'bulk-' || n, 1, 1, 1, 0, 'bulk', 3, {0} + n * 1000, {0} + n * 1000, 1.0, 1.0, 900, " +
                "'recognition', 0, 2, 2, {0}, 0, 1 FROM seq;", start);
        }
        using var client = await LearnerClientAsync(factory, "learner");
        var stopwatch = Stopwatch.StartNew();
        using var body = await JsonAsync(await client.GetAsync("/api/learn/stats", CancellationToken.None));
        stopwatch.Stop();
        var root = body.RootElement;
        Assert.Equal(50000, root.GetProperty("totals").GetProperty("answers").GetInt32());
        // One session id, one card, every rating a hit: one group, and its first row is a hit, so
        // the card-level headline is 1 / 0 while `answers` stays 50 000.
        Assert.Equal(1, root.GetProperty("totals").GetProperty("firstTry").GetInt32());
        Assert.Equal(0, root.GetProperty("totals").GetProperty("secondLook").GetInt32());
        Assert.Equal(50000,
            root.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("answers").GetInt32()));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"stats took {stopwatch.Elapsed.TotalSeconds:F1}s for 50k rows");
    }

    // FirstRowRatings is derived in SQL by LoadAsync; the pure tests that do not assert the
    // first-try headline pass it empty, and the two that do go through the API.
    private static LearnStats.StatsInput Input(IReadOnlyList<LearnStats.StatsRow> rows) =>
        new(rows,
            [],
            [new LearnStats.StatsCardRef(
                new Card { Id = 1, UserId = 1, SenseId = 1, Direction = "es_en", State = CardState.Review },
                1)],
            [new LearnStats.StatsWord(1, "es-abuela-01", "la", "abuela", "grandmother")],
            new Dictionary<int, string> { [1] = "es_en" },
            [],
            0.90);

    private static LearnStats.StatsRow Row(LearnerClock clock, DateOnly localDate)
    {
        var at = clock.StartOfLocalDateUtc(localDate).AddHours(12).ToUnixTimeMilliseconds();
        return new LearnStats.StatsRow(1, 1, "s", Rating.Good, at, at, 500, "recognition", null, false, true,
            CardState.Review);
    }

    private static DateTimeOffset StartOf(int year, int month, int day) =>
        Madrid.StartOfLocalDateUtc(new DateOnly(year, month, day));

    private static DateTimeOffset EndOf(int year, int month, int day) =>
        Madrid.EndOfLocalDateUtc(new DateOnly(year, month, day));

    private static DateTimeOffset LocalNoon(int year, int month, int day) => StartOf(year, month, day).AddHours(12);

    private static void AddLog(PalabrasDbContext database, int cardId, int wordId, Rating rating,
        DateTimeOffset reviewedAt, int? durationMs = 800, string mode = "recognition", bool? isCorrectForm = null,
        bool overridden = false, bool countsForSrs = true, CardState stateBefore = CardState.Review,
        string? sessionId = "s", DateTimeOffset? receivedAt = null, int userId = 1)
    {
        var reviewedAtMs = reviewedAt.ToUnixTimeMilliseconds();
        database.ReviewLogs.Add(new ReviewLog
        {
            ClientAttemptId = Guid.NewGuid().ToString("D"),
            UserId = userId,
            WordId = wordId,
            CardId = cardId,
            CardEpoch = 0,
            SessionId = sessionId,
            Rating = rating,
            ReviewedAtMs = reviewedAtMs,
            ReceivedAtMs = (receivedAt ?? reviewedAt).ToUnixTimeMilliseconds(),
            ElapsedDays = 1,
            IntervalAfterDays = 1,
            DurationMs = durationMs,
            Mode = mode,
            IsCorrectForm = isCorrectForm,
            Overridden = overridden,
            StateBefore = stateBefore,
            StateAfter = CardState.Review,
            DueAfterMs = reviewedAtMs,
            CountsForSrs = countsForSrs
        });
    }

    private static void AddWord(PalabrasDbContext database, int id, string key, string headword,
        string status = "approved", string? article = null, string enGloss = "gloss")
    {
        database.Words.Add(new Word
        {
            Id = id, Key = key, Headword = headword, Article = article, Pos = "noun", Cefr = "A1",
            RegionScope = "ES", Register = "neutral", Status = status, ContentHash = "hash",
            ProvenanceMethod = "manual"
        });
        database.Senses.Add(new Sense
        {
            Id = id, WordId = id, SenseOrder = 0, EnGloss = enGloss, EnAccepted = "[]", EsAccepted = "[]"
        });
        // S6b: the current version's snapshot records which sense ordinals are live.
        SetSnapshot(database, id, 1);
    }

    private static void AddSense(PalabrasDbContext database, int senseId, int wordId, int senseOrder,
        string enGloss = "gloss")
    {
        database.Senses.Add(new Sense
        {
            Id = senseId, WordId = wordId, SenseOrder = senseOrder, EnGloss = enGloss,
            EnAccepted = "[]", EsAccepted = "[]"
        });
    }

    private static void SetSnapshot(PalabrasDbContext database, int wordId, int senseCount, int version = 1)
    {
        var senses = Enumerable.Range(0, senseCount)
            .Select(index => (object)new { gloss = $"gloss-{wordId}-{index}" }).ToArray();
        database.WordRevisions.Add(new WordRevision
        {
            WordId = wordId, Version = version, ActorUserId = null, Action = "import",
            SnapshotJson = JsonSerializer.Serialize(new { senses }), CreatedAt = Now.ToUnixTimeMilliseconds()
        });
    }

    private static void AddCard(PalabrasDbContext database, int id, int senseId, int userId = 1,
        string direction = "es_en", CardState state = CardState.New, int reps = 0, string? suspended = null,
        bool leech = false, double? stability = null, string? introduced = null)
    {
        database.Cards.Add(new Card
        {
            Id = id, UserId = userId, SenseId = senseId, Direction = direction, State = state,
            DueMs = Now.ToUnixTimeMilliseconds(), Epoch = 0, Reps = reps, SuspendedReason = suspended,
            IsLeech = leech, Stability = stability, IntroducedLocalDate = introduced,
            CreatedAt = Now.ToUnixTimeMilliseconds()
        });
    }

    private static async Task<HttpClient> LearnerClientAsync(StatsApplicationFactory factory, string username)
    {
        var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username, password = "test-password" })
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        return client;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

    private static async Task AssertJsonError(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = await JsonAsync(response);
        Assert.Equal(error, body.RootElement.GetProperty("error").GetString());
    }

    private sealed class StatsApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash("test-password");
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode",
            "palabras-stats-" + Guid.NewGuid().ToString("N"));
        private readonly FakeTimeProvider _time;

        internal StatsApplicationFactory(DateTimeOffset now, Action<PalabrasDbContext>? seed = null)
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
