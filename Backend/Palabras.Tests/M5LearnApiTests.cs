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
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

// Contract tests for plan §4. Time comes from FakeTimeProvider, the scheduler from
// FakeScheduler (one reps value per instance, so a fresh one per test), and every
// learner call goes through the real cookie auth + X-Palabras middleware.
public sealed class M5LearnApiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T09:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset StartOfToday = new LearnerClock(new FakeTimeProvider(Now), "Europe/Madrid").StartOfLocalDayUtc;

    [Fact]
    public async Task PlanReturnsTheFullContractWithDueLearningFirstThenNew()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", article: "la", gender: "feminine",
                enGloss: "grandmother", esAccepted: "[\"la abuela\",\"abuela\"]", enAccepted: "[\"grandmother\"]",
                exampleEs: "Mi abuela vive en el pueblo.", exampleEn: "My grandmother lives in the village.");
            AddWord(database, 2, "es-coger-01", "coger", pos: "verb",
                enGloss: "to take, to catch", exampleEs: "Coge el autobús.", exampleEn: "Take the bus.");
            AddWord(database, 3, "es-perro-01", "perro", status: "pending_review", article: "el", gender: "masculine",
                enGloss: "dog", exampleEs: "El perro ladra.", exampleEn: "The dog barks.");
            AddWord(database, 4, "es-zapato-01", "zapato", cefr: "A2", article: "el", gender: "masculine",
                enGloss: "shoe", exampleEs: "El zapato es negro.", exampleEn: "The shoe is black.");
            AddWord(database, 5, "es-gato-01", "gato", status: "pending_review", article: "el", gender: "masculine",
                enGloss: "cat", exampleEs: "El gato duerme.", exampleEn: "The cat sleeps.");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddSeconds(-5).ToUnixTimeMilliseconds(), reps: 2, stability: 30, epoch: 2);
            AddCard(database, 2, 1, direction: "en_es", state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
            AddCard(database, 3, 2, state: CardState.Learning, dueMs: Now.AddSeconds(-6).ToUnixTimeMilliseconds());
            AddCard(database, 4, 5, state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
            AddCard(database, 5, 4, state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
            // Pending word with a due review: the approved-word join must keep it out.
            AddCard(database, 6, 3, state: CardState.Review, dueMs: Now.AddSeconds(-9).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);

        using var response = await client.GetAsync("/api/learn/session/plan", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.True(Guid.TryParse(body.RootElement.GetProperty("sessionId").GetString(), out _));
        Assert.Equal(Now, body.RootElement.GetProperty("generatedAtUtc").GetDateTimeOffset().ToUniversalTime());
        Assert.Equal("2026-10-18", body.RootElement.GetProperty("localDate").GetString());

        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 3, 1, 5 }, items.Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
        // Plan items carry cards.epoch verbatim so a flush can be rejected as stale_epoch.
        Assert.Equal(new[] { 0, 2, 0 }, items.Select(item => item.GetProperty("cardEpoch").GetInt32()).ToArray());
        Assert.Equal(new[] { false, false, true }, items.Select(item => item.GetProperty("isNew").GetBoolean()).ToArray());
        Assert.Equal(new[] { "recognition", "recognition", "recognition" },
            items.Select(item => item.GetProperty("mode").GetString()).ToArray());

        var due = items[1];
        Assert.Equal("es_en", due.GetProperty("direction").GetString());
        var word = due.GetProperty("word");
        Assert.Equal("es-abuela-01", word.GetProperty("key").GetString());
        Assert.Equal("abuela", word.GetProperty("headword").GetString());
        Assert.Equal("la", word.GetProperty("article").GetString());
        Assert.Equal("feminine", word.GetProperty("gender").GetString());
        Assert.Equal("noun", word.GetProperty("pos").GetString());
        var sense = due.GetProperty("sense");
        Assert.Equal("grandmother", sense.GetProperty("gloss").GetString());
        Assert.Equal("Mi abuela vive en el pueblo.", sense.GetProperty("exampleEs").GetString());
        Assert.Equal("My grandmother lives in the village.", sense.GetProperty("exampleEn").GetString());
        Assert.Equal("Mi ____ vive en el pueblo.", sense.GetProperty("clozeEs").GetString());
        Assert.Equal(new[] { "la abuela", "abuela" },
            sense.GetProperty("acceptedEs").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "grandmother" },
            sense.GetProperty("acceptedEn").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.False(sense.GetProperty("accentsAreThePoint").GetBoolean());
        Assert.Equal("ES", due.GetProperty("notes").GetProperty("region").GetString());
        Assert.Equal("neutral", due.GetProperty("notes").GetProperty("register").GetString());

        // A conjugated example has no headword match: cloze is omitted, mode stays recognition.
        Assert.False(items[0].GetProperty("sense").TryGetProperty("clozeEs", out _));
        // Fallbacks: EN accepted answers are the gloss split on ", ".
        Assert.Equal(new[] { "to take", "to catch" },
            items[0].GetProperty("sense").GetProperty("acceptedEn").EnumerateArray().Select(value => value.GetString()).ToArray());

        var counts = body.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("due").GetInt32());
        Assert.Equal(1, counts.GetProperty("newOffered").GetInt32());
        Assert.Equal(2, counts.GetProperty("backlog").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.False(body.RootElement.TryGetProperty("nextDueUtc", out _));
    }

    [Fact]
    public async Task LearnRoutesRejectAnonymousReviewerAndHeaderlessWrites()
    {
        using var factory = new LearnApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        using var unauthorized = await anonymous.GetAsync("/api/learn/session/plan", CancellationToken.None);
        await AssertJsonError(unauthorized, HttpStatusCode.Unauthorized, "unauthorized");

        using var reviewer = await LearnerClientAsync(factory, "reviewer");
        using var forbidden = await reviewer.GetAsync("/api/learn/session/plan", CancellationToken.None);
        await AssertJsonError(forbidden, HttpStatusCode.Forbidden, "forbidden");

        using var owner = await LearnerClientAsync(factory);
        using var headerless = new HttpRequestMessage(HttpMethod.Post, "/api/learn/attempts")
        {
            Content = JsonContent.Create(new { attempts = Array.Empty<object>() })
        };
        using var missing = await owner.SendAsync(headerless, CancellationToken.None);
        await AssertJsonError(missing, HttpStatusCode.BadRequest, "missing_x_palabras");
    }

    [Fact]
    public async Task ReplayedAttemptIsDuplicateAndNeverDoubleApplies()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), reps: 3);
        });
        using var client = await LearnerClientAsync(factory);
        var attempt = new
        {
            clientAttemptId = Guid.NewGuid().ToString(), cardId = 1, cardEpoch = 0, sessionId = "session-1",
            mode = "recognition", rating = 3, isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null,
            overridden = false, feedbackKind = (string?)null, answerText = (string?)null,
            durationMs = 1200, reviewedAtUtc = Now.AddHours(-1)
        };

        using var first = await PostJsonAsync(client, "/api/learn/attempts", new { attempts = new[] { attempt } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var body = await JsonAsync(first))
        {
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal(attempt.clientAttemptId, result.GetProperty("clientAttemptId").GetString());
            Assert.Equal("applied", result.GetProperty("status").GetString());
            var card = result.GetProperty("card");
            Assert.Equal(1, card.GetProperty("id").GetInt32());
            Assert.Equal((int)CardState.Review, card.GetProperty("state").GetInt32());
            Assert.Equal(Now.AddHours(-1).AddDays(1), card.GetProperty("dueUtc").GetDateTimeOffset().ToUniversalTime());
            Assert.Equal(1, card.GetProperty("intervalDays").GetDouble());
            Assert.False(card.GetProperty("leech").GetBoolean());
        }
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal(4, card.Reps);
            Assert.Equal(Now.AddHours(-1).ToUnixTimeMilliseconds(), card.LastReviewMs);
            Assert.Equal(Now.AddHours(-1).AddDays(1).ToUnixTimeMilliseconds(), card.DueMs);
            // Introduced on the first applied review, in the learner's local date.
            Assert.Equal("2026-10-18", card.IntroducedLocalDate);
            Assert.Equal(1, await database.ReviewLogs.CountAsync());
        }

        using var replay = await PostJsonAsync(client, "/api/learn/attempts", new { attempts = new[] { attempt } });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var body = await JsonAsync(replay))
        {
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("duplicate", result.GetProperty("status").GetString());
            Assert.False(result.TryGetProperty("card", out _));
        }
        using (var database = factory.OpenDatabase())
        {
            Assert.Equal(4, (await database.Cards.SingleAsync(candidate => candidate.Id == 1)).Reps);
            Assert.Equal(1, await database.ReviewLogs.CountAsync());
        }
    }

    [Fact]
    public async Task LateOfflineAttemptIsStoredOutOfOrderWithoutMovingTheCard()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddDays(1).ToUnixTimeMilliseconds(),
                reps: 2, lastReviewMs: Now.AddHours(-1).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                new
                {
                    clientAttemptId = Guid.NewGuid().ToString(), cardId = 1, cardEpoch = 0, sessionId = "s",
                    mode = "recognition", rating = 3, isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null,
                    overridden = false, feedbackKind = (string?)null, answerText = (string?)null,
                    durationMs = 900_000, reviewedAtUtc = Now.AddHours(-2)
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var result = body.RootElement.GetProperty("results").EnumerateArray().Single();
            Assert.Equal("out_of_order", result.GetProperty("status").GetString());
            Assert.False(result.TryGetProperty("card", out _));
        }
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal(2, card.Reps);
            Assert.Equal(Now.AddHours(-1).ToUnixTimeMilliseconds(), card.LastReviewMs);
            Assert.Equal(Now.AddDays(1).ToUnixTimeMilliseconds(), card.DueMs);
            var log = await database.ReviewLogs.SingleAsync();
            Assert.False(log.CountsForSrs);
            Assert.Equal(Now.AddHours(-2).ToUnixTimeMilliseconds(), log.ReviewedAtMs);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), log.ReceivedAtMs);
            Assert.Equal(600_000, log.DurationMs);
        }
    }

    [Fact]
    public async Task StaleEpochIsRecordedAndNotApplied()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), epoch: 3, reps: 1);
        });
        using var client = await LearnerClientAsync(factory);
        using var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                new
                {
                    clientAttemptId = Guid.NewGuid().ToString(), cardId = 1, cardEpoch = 2, sessionId = "s",
                    mode = "recognition", rating = 3, isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null,
                    overridden = false, feedbackKind = (string?)null, answerText = (string?)null,
                    durationMs = 900, reviewedAtUtc = Now.AddMinutes(-30)
                }
            }
        });
        using (var body = await JsonAsync(response))
        {
            Assert.Equal("stale_epoch", body.RootElement.GetProperty("results").EnumerateArray().Single()
                .GetProperty("status").GetString());
        }
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal(3, card.Epoch);
            Assert.Equal(1, card.Reps);
            Assert.Null(card.LastReviewMs);
            var log = await database.ReviewLogs.SingleAsync();
            Assert.Equal(2, log.CardEpoch);
            Assert.False(log.CountsForSrs);
            Assert.Equal(CardState.Review, log.StateBefore);
        }
    }

    [Fact]
    public async Task SuspendedCardIsNeverPlannedAndIsRejectedOnAttempt()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), suspended: "manual");
        });
        using var client = await LearnerClientAsync(factory);
        using var plan = await client.GetAsync("/api/learn/session/plan", CancellationToken.None);
        using (var body = await JsonAsync(plan))
        {
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(0, body.RootElement.GetProperty("counts").GetProperty("due").GetInt32());
        }
        using var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                new
                {
                    clientAttemptId = Guid.NewGuid().ToString(), cardId = 1, cardEpoch = 0, sessionId = "s",
                    mode = "recognition", rating = 3, isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null,
                    overridden = false, feedbackKind = (string?)null, answerText = (string?)null,
                    durationMs = 900, reviewedAtUtc = Now.AddMinutes(-10)
                }
            }
        });
        using (var body = await JsonAsync(response))
        {
            Assert.Equal("suspended", body.RootElement.GetProperty("results").EnumerateArray().Single()
                .GetProperty("status").GetString());
        }
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal("manual", card.SuspendedReason);
            Assert.Equal(0, card.Reps);
            Assert.False((await database.ReviewLogs.SingleAsync()).CountsForSrs);
        }
    }

    [Fact]
    public async Task LeechSuspendsOnTheEighthLapseOnly()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-zapato-01", "zapato");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), reps: 9, lapses: 7);
            AddCard(database, 2, 2, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), reps: 9, lapses: 6);
        });
        using var client = await LearnerClientAsync(factory);
        using var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                Attempt(Guid.NewGuid().ToString(), 1, Now.AddMinutes(-3), rating: 1),
                Attempt(Guid.NewGuid().ToString(), 2, Now.AddMinutes(-2), rating: 1)
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(new[] { "applied", "applied" }, results.Select(row => row.GetProperty("status").GetString()).ToArray());
            Assert.True(results[0].GetProperty("card").GetProperty("leech").GetBoolean());
            Assert.False(results[1].GetProperty("card").GetProperty("leech").GetBoolean());
        }
        using (var database = factory.OpenDatabase())
        {
            var leech = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal(8, leech.Lapses);
            Assert.True(leech.IsLeech);
            Assert.Equal("leech", leech.SuspendedReason);
            var active = await database.Cards.SingleAsync(candidate => candidate.Id == 2);
            Assert.Equal(7, active.Lapses);
            Assert.False(active.IsLeech);
            Assert.Null(active.SuspendedReason);
        }
    }

    [Fact]
    public async Task BacklogAboveThresholdOffersNoNewUntilMaxNewOverrides()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            for (var id = 1; id <= 151; id++)
            {
                AddWord(database, id, $"es-word-{id:D3}", $"palabra{id}");
                AddCard(database, id, id, state: CardState.Review, dueMs: Now.AddSeconds(-id).ToUnixTimeMilliseconds(), stability: 10);
            }
            AddWord(database, 152, "es-nuevo-152", "nueva", cefr: "A1");
            AddCard(database, 152, 152, state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var plan = await client.GetAsync("/api/learn/session/plan", CancellationToken.None);
        using (var body = await JsonAsync(plan))
        {
            var counts = body.RootElement.GetProperty("counts");
            Assert.Equal(151, counts.GetProperty("due").GetInt32());
            Assert.Equal(151, counts.GetProperty("backlog").GetInt32());
            Assert.Equal(0, counts.GetProperty("newOffered").GetInt32());
            Assert.Equal(new[] { "backlog_no_new" },
                body.RootElement.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal(151, body.RootElement.GetProperty("items").GetArrayLength());
            Assert.DoesNotContain(body.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("isNew").GetBoolean());
        }

        // H11 "add new anyway": the explicit request outranks the backlog rule and the budget trim.
        using var overridePlan = await client.GetAsync("/api/learn/session/plan?maxNew=12", CancellationToken.None);
        using (var body = await JsonAsync(overridePlan))
        {
            Assert.Equal(1, body.RootElement.GetProperty("counts").GetProperty("newOffered").GetInt32());
            Assert.Empty(body.RootElement.GetProperty("warnings").EnumerateArray());
            Assert.Equal(152, body.RootElement.GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task EnEsStaysWithheldUntilSiblingReachedReviewAndSucceededYesterday()
    {
        var yesterday = StartOfToday.AddMilliseconds(-1).ToUnixTimeMilliseconds();
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-unlocked-01", "abuela");
            AddWord(database, 2, "es-today-02", "perro");
            AddWord(database, 3, "es-learning-03", "zapato");
            // Unlocked: es_en in Review with a successful review before local midnight.
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddSeconds(-5).ToUnixTimeMilliseconds());
            AddCard(database, 2, 1, direction: "en_es", state: CardState.Review, dueMs: Now.AddSeconds(-4).ToUnixTimeMilliseconds());
            // Withheld: the successful review happened today.
            AddCard(database, 3, 2, state: CardState.Review, dueMs: Now.AddSeconds(-3).ToUnixTimeMilliseconds());
            AddCard(database, 4, 2, direction: "en_es", state: CardState.Review, dueMs: Now.AddSeconds(-2).ToUnixTimeMilliseconds());
            // Withheld: es_en never reached Review.
            AddCard(database, 5, 3, state: CardState.Learning, dueMs: Now.AddSeconds(-1).ToUnixTimeMilliseconds());
            AddCard(database, 6, 3, direction: "en_es", state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
            database.ReviewLogs.AddRange(
                new ReviewLog
                {
                    ClientAttemptId = "yesterday-success", UserId = 1, WordId = 1, CardId = 1, CardEpoch = 0,
                    Rating = Rating.Good, ReviewedAtMs = yesterday, ReceivedAtMs = yesterday,
                    Mode = "recognition", CountsForSrs = true
                },
                new ReviewLog
                {
                    ClientAttemptId = "today-success", UserId = 1, WordId = 2, CardId = 3, CardEpoch = 0,
                    Rating = Rating.Good, ReviewedAtMs = StartOfToday.ToUnixTimeMilliseconds(), ReceivedAtMs = StartOfToday.ToUnixTimeMilliseconds(),
                    Mode = "recognition", CountsForSrs = true
                });
        });
        using var client = await LearnerClientAsync(factory);
        using var plan = await client.GetAsync("/api/learn/session/plan", CancellationToken.None);
        using var body = await JsonAsync(plan);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        // Learning first, then due order; only card 2 (en_es) is unlocked.
        Assert.Equal(new[] { 5, 1, 2, 3 }, items.Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
        Assert.Equal(new[] { "es_en", "es_en", "en_es", "es_en" },
            items.Select(item => item.GetProperty("direction").GetString()).ToArray());
        Assert.Equal(4, body.RootElement.GetProperty("counts").GetProperty("due").GetInt32());
    }

    [Fact]
    public async Task ProductionCardsAlternateTypedAndClozeWhenTheHeadwordIsInTheExample()
    {
        var yesterday = StartOfToday.AddMilliseconds(-1).ToUnixTimeMilliseconds();
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", exampleEs: "Mi abuela vive aquí.", exampleEn: "My grandmother lives here.");
            AddWord(database, 2, "es-zapato-02", "zapato", exampleEs: "El zapato es negro.", exampleEn: "The shoe is black.");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddDays(1).ToUnixTimeMilliseconds());
            AddCard(database, 2, 1, direction: "en_es", state: CardState.Review, reps: 1,
                dueMs: Now.AddSeconds(-2).ToUnixTimeMilliseconds());
            AddCard(database, 3, 2, state: CardState.Review, dueMs: Now.AddDays(1).ToUnixTimeMilliseconds());
            AddCard(database, 4, 2, direction: "en_es", state: CardState.Review, reps: 2,
                dueMs: Now.AddSeconds(-1).ToUnixTimeMilliseconds());
            database.ReviewLogs.AddRange(Success("sibling-1", 1, 1, yesterday), Success("sibling-2", 2, 3, yesterday));
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None));
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 2, 4 }, items.Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
        Assert.Equal(new[] { "cloze", "typed" },
            items.Select(item => item.GetProperty("mode").GetString()).ToArray());
        Assert.Equal("Mi ____ vive aquí.", items[0].GetProperty("sense").GetProperty("clozeEs").GetString());
    }

    [Fact]
    public async Task RetainedRemovedSenseIsInvisibleToEveryLearnerPath()
    {
        // S6b keeps a sense row the content file dropped and leaves a manual/leech reason alone,
        // so `suspended_reason` alone cannot identify removal. The current-version snapshot can:
        // ordinal 1 is outside a one-sense snapshot, so both of its cards must be unreachable.
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-coger-01", "coger", pos: "verb", enGloss: "to take", exampleEs: "Coge el autobús.");
            AddSense(database, 11, 1, 1, enGloss: "to catch (transport)");
            SetSnapshot(database, 1, 1);
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddSeconds(-1).ToUnixTimeMilliseconds(), stability: 10);
            AddCard(database, 2, 11, state: CardState.Review, dueMs: Now.AddSeconds(-2).ToUnixTimeMilliseconds(),
                suspended: "manual", stability: 100);
            AddCard(database, 3, 11, direction: "en_es", state: CardState.Review,
                dueMs: Now.AddSeconds(-3).ToUnixTimeMilliseconds());
            // Would unlock card 3 if the ordinal gate were missing.
            database.ReviewLogs.Add(Success("removed-sibling", 1, 2, StartOfToday.AddMilliseconds(-1).ToUnixTimeMilliseconds()));
        });
        using var client = await LearnerClientAsync(factory);

        using (var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None)))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { 1 }, items.Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
            Assert.Equal(1, body.RootElement.GetProperty("counts").GetProperty("due").GetInt32());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/learn/words", CancellationToken.None)))
        {
            var word = body.RootElement.GetProperty("items").EnumerateArray().Single();
            // Gloss from the active ordinal only, and the removed sense's stability-100 card
            // must not lift the stage to "dominada".
            Assert.Equal("to take", word.GetProperty("gloss").GetString());
            Assert.Equal("reciente", word.GetProperty("stage").GetString());
        }
        using (var unsuspend = await PostJsonAsync(client, "/api/learn/cards/2/unsuspend", new { }))
        {
            await AssertJsonError(unsuspend, HttpStatusCode.Conflict, "not_suspendable");
        }
        using (var attempt = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[] { Attempt(Guid.NewGuid().ToString(), 3, Now.AddMinutes(-5)) }
        }))
        {
            using var body = await JsonAsync(attempt);
            Assert.Equal("suspended", body.RootElement.GetProperty("results").EnumerateArray().Single()
                .GetProperty("status").GetString());
        }
        using (var database = factory.OpenDatabase())
        {
            Assert.Equal("manual", (await database.Cards.SingleAsync(card => card.Id == 2)).SuspendedReason);
            var removed = await database.Cards.SingleAsync(card => card.Id == 3);
            Assert.Null(removed.SuspendedReason);
            Assert.Equal(0, removed.Reps);
            // The attempt is recorded for audit but never counts towards the scheduler.
            var rejected = await database.ReviewLogs.SingleAsync(log => log.CardId == 3);
            Assert.False(rejected.CountsForSrs);
        }
    }

    [Fact]
    public async Task EveryOrdinalInsideTheSnapshotStaysActive()
    {
        // Negative control for the gate above: two snapshot entries mean both ordinals are live.
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-coger-01", "coger", pos: "verb");
            AddSense(database, 11, 1, 1);
            SetSnapshot(database, 1, 2);
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddSeconds(-1).ToUnixTimeMilliseconds());
            AddCard(database, 2, 11, state: CardState.Review, dueMs: Now.AddSeconds(-2).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None));
        Assert.Equal(new[] { 2, 1 }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
    }

    [Fact]
    public async Task EpochFromPlanBecomesStaleWhenContentChangesBeforeFlush()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        int plannedEpoch;
        using (var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None)))
        {
            plannedEpoch = body.RootElement.GetProperty("items").EnumerateArray().Single()
                .GetProperty("cardEpoch").GetInt32();
        }
        Assert.Equal(0, plannedEpoch);
        // An import/retire between plan fetch and flush bumps the epoch (S6b invalidation).
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            card.Epoch = 1;
            await database.SaveChangesAsync(CancellationToken.None);
        }
        using (var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                new
                {
                    clientAttemptId = Guid.NewGuid().ToString(), cardId = 1, cardEpoch = plannedEpoch,
                    sessionId = "s", mode = "recognition", rating = 3, isCorrectMeaning = (bool?)null,
                    isCorrectForm = (bool?)null, overridden = false, feedbackKind = (string?)null,
                    answerText = (string?)null, durationMs = 900, reviewedAtUtc = Now.AddMinutes(-5)
                }
            }
        }))
        {
            using var body = await JsonAsync(response);
            Assert.Equal("stale_epoch", body.RootElement.GetProperty("results").EnumerateArray().Single()
                .GetProperty("status").GetString());
        }
        using (var database = factory.OpenDatabase())
        {
            var card = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal(1, card.Epoch);
            Assert.Equal(0, card.Reps);
            Assert.Null(card.LastReviewMs);
            Assert.False((await database.ReviewLogs.SingleAsync()).CountsForSrs);
        }
    }

    [Fact]
    public async Task ForeignAndUnknownCardIdsAreIndistinguishableOnAttempt()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-zapato-02", "zapato");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
            AddCard(database, 2, 2, userId: 2, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using (var response = await PostJsonAsync(client, "/api/learn/attempts", new
        {
            attempts = new[]
            {
                Attempt(Guid.NewGuid().ToString(), 2, Now.AddMinutes(-5)),
                Attempt(Guid.NewGuid().ToString(), 9999, Now.AddMinutes(-4))
            }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await JsonAsync(response);
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
            // Same status for someone else's card and for a card that does not exist: no disclosure.
            Assert.Equal(new[] { "suspended", "suspended" },
                results.Select(row => row.GetProperty("status").GetString()).ToArray());
            Assert.All(results, row => Assert.False(row.TryGetProperty("card", out _)));
        }
        using (var database = factory.OpenDatabase())
        {
            var foreign = await database.Cards.SingleAsync(candidate => candidate.Id == 2);
            Assert.Equal(0, foreign.Reps);
            Assert.Null(foreign.LastReviewMs);
            Assert.Empty(await database.ReviewLogs.ToListAsync());
        }
    }

    [Fact]
    public async Task MaxNewCannotRaiseTheDailyNewCap()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            database.UserSettings.Add(new UserSettings { UserId = 1 });
            for (var id = 1; id <= 20; id++)
            {
                AddWord(database, id, $"es-new-{id:D3}", $"nueva{id}");
                AddCard(database, id, id, state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
            }
            // Five cards were introduced today already: 12 - 5 = 7 remain, whatever the client asks.
            for (var id = 21; id <= 25; id++)
            {
                AddWord(database, id, $"es-done-{id:D3}", $"hecha{id}");
                AddCard(database, id, id, state: CardState.Review, dueMs: Now.AddDays(1).ToUnixTimeMilliseconds(),
                    stability: 5, introduced: "2026-10-18");
            }
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan?maxNew=100000",
            CancellationToken.None));
        Assert.Equal(7, body.RootElement.GetProperty("counts").GetProperty("newOffered").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(body.RootElement.GetProperty("items").EnumerateArray(),
            item => Assert.True(item.GetProperty("isNew").GetBoolean()));
    }

    [Fact]
    public async Task QueueQueryTranslatesEntirelyToSqliteSql()
    {
        await using var rig = new SrsTestDatabase();
        var database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        var clock = new LearnerClock(new FakeTimeProvider(Now), "Europe/Madrid");
        var bounds = QueueBounds.From(clock, 1);
        database.Users.Add(new User { Id = 1, Username = "owner", Role = "owner", PasswordHash = "hash" });
        database.Words.Add(new Word
        {
            Id = 1, Key = "es-abuela-01", Headword = "abuela", Pos = "noun", Cefr = "A1",
            RegionScope = "ES", Register = "neutral", ContentHash = "hash", ProvenanceMethod = "manual", Status = "approved"
        });
        database.Words.Add(new Word
        {
            Id = 2, Key = "es-zapato-02", Headword = "zapato", Pos = "noun", Cefr = "A1",
            RegionScope = "ES", Register = "neutral", ContentHash = "hash", ProvenanceMethod = "manual", Status = "approved"
        });
        database.Senses.Add(new Sense { Id = 1, WordId = 1, SenseOrder = 0 });
        database.Senses.Add(new Sense { Id = 2, WordId = 2, SenseOrder = 0 });
        SetSnapshot(database, 1, 1);
        SetSnapshot(database, 2, 1);
        database.Cards.Add(new Card
        {
            Id = 1, UserId = 1, SenseId = 1, State = CardState.Review, DueMs = Now.ToUnixTimeMilliseconds()
        });
        await database.SaveChangesAsync(CancellationToken.None);

        // Two active senses so the S6b gate renders as an IN list, not a single equality.
        var active = await ActiveSenses.LoadAsync(database, null, CancellationToken.None);
        var dueSql = LearnQueries.Due(database, bounds, active, 200).ToQueryString();
        var newSql = LearnQueries.New(database, bounds, active, 12).ToQueryString();
        foreach (var sql in new[] { dueSql, newSql })
        {
            Assert.Contains("WHERE", sql);
            Assert.Contains("\"user_id\" = ", sql);
            Assert.Contains("\"suspended_reason\" IS NULL", sql);
            Assert.Contains("\"status\" = 'approved'", sql);
            Assert.Contains("\"due_ms\" <=", sql);
            Assert.Contains("\"buried_until_local_date\" <", sql);
            // S6b active-sense gate: the retained-removed-sense filter is still a translated IN list.
            Assert.Contains("\"sense_id\" IN (", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.Contains("LIMIT", sql);
        }
        Assert.Contains("EXISTS", dueSql);
        Assert.Contains("\"reviewed_at_ms\" <", dueSql);
        Assert.Contains("\"due_ms\"", dueSql);
        Assert.Contains("INNER JOIN \"senses\"", newSql);
        Assert.Contains("\"cefr\"", newSql);

        // Negative control: a UTC->local conversion inside a filter cannot translate, so the
        // boundary has to be a plain long comparison from LearnerClock.
        Assert.Throws<InvalidOperationException>(() => database.Cards
            .Where(card => clock.LocalDateOf(DateTimeOffset.FromUnixTimeMilliseconds(card.DueMs)) == clock.LocalToday)
            .ToQueryString());
    }

    [Theory]
    [InlineData("2026-10-25T21:30:00Z", "2026-10-25", "2026-10-25T22:59:59.999Z")]
    [InlineData("2027-03-28T21:00:00Z", "2027-03-28", "2027-03-28T21:59:59.999Z")]
    public async Task PlanBoundaryIsLocalMidnightAcrossDst(string now, string localDate, string endOfDay)
    {
        var instant = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(endOfDay, CultureInfo.InvariantCulture);
        using var factory = new LearnApplicationFactory(instant, database =>
        {
            AddWord(database, 1, "es-inside-01", "abuela");
            AddWord(database, 2, "es-after-02", "zapato");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: end.ToUnixTimeMilliseconds());
            AddCard(database, 2, 2, state: CardState.Review, dueMs: end.AddMilliseconds(1).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var response = await client.GetAsync("/api/learn/session/plan", CancellationToken.None);
        using var body = await JsonAsync(response);
        Assert.Equal(localDate, body.RootElement.GetProperty("localDate").GetString());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1 }, items.Select(item => item.GetProperty("cardId").GetInt32()).ToArray());
        Assert.Equal(1, body.RootElement.GetProperty("counts").GetProperty("backlog").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("nextDueUtc", out _));
    }

    [Fact]
    public async Task EmptyPlanReportsTheNextDueInstant()
    {
        var end = new LearnerClock(new FakeTimeProvider(Now), "Europe/Madrid").EndOfLocalDayUtc.AddMilliseconds(1);
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: end.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None));
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(end, body.RootElement.GetProperty("nextDueUtc").GetDateTimeOffset().ToUniversalTime());
    }

    [Fact]
    public async Task BurySuspendAndUnsuspendFollowTheCardContract()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-perro-02", "perro");
            AddWord(database, 3, "es-zapato-03", "zapato");
            AddWord(database, 4, "es-casa-04", "casa");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
            AddCard(database, 2, 1, direction: "en_es", state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(),
                suspended: "word_not_approved");
            AddCard(database, 3, 2, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), suspended: "sense_removed");
            AddCard(database, 4, 3, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds(), suspended: "leech",
                leech: true, lapses: 8);
            AddCard(database, 5, 4, userId: 2, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);

        using (var bury = await PostJsonAsync(client, "/api/learn/cards/1/bury", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, bury.StatusCode);
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/learn/session/plan", CancellationToken.None)))
        {
            Assert.DoesNotContain(body.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("cardId").GetInt32() == 1);
        }
        using (var missing = await PostJsonAsync(client, "/api/learn/cards/9999/bury", new { }))
        {
            await AssertJsonError(missing, HttpStatusCode.NotFound, "not_found");
        }
        using (var foreign = await PostJsonAsync(client, "/api/learn/cards/5/suspend", new { }))
        {
            await AssertJsonError(foreign, HttpStatusCode.NotFound, "not_found");
        }
        using (var notApproved = await PostJsonAsync(client, "/api/learn/cards/2/suspend", new { }))
        {
            await AssertJsonError(notApproved, HttpStatusCode.Conflict, "not_suspendable");
        }
        using (var removed = await PostJsonAsync(client, "/api/learn/cards/3/unsuspend", new { }))
        {
            await AssertJsonError(removed, HttpStatusCode.Conflict, "not_suspendable");
        }
        using (var suspend = await PostJsonAsync(client, "/api/learn/cards/1/suspend", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        }
        using (var unsuspend = await PostJsonAsync(client, "/api/learn/cards/1/unsuspend", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, unsuspend.StatusCode);
        }
        using (var leech = await PostJsonAsync(client, "/api/learn/cards/4/unsuspend", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, leech.StatusCode);
        }
        using (var database = factory.OpenDatabase())
        {
            var buried = await database.Cards.SingleAsync(candidate => candidate.Id == 1);
            Assert.Equal("2026-10-19", buried.BuriedUntilLocalDate);
            Assert.Null(buried.SuspendedReason);
            var revived = await database.Cards.SingleAsync(candidate => candidate.Id == 4);
            Assert.Null(revived.SuspendedReason);
            Assert.False(revived.IsLeech);
            Assert.Equal(8, revived.Lapses);
            Assert.Equal("word_not_approved", (await database.Cards.SingleAsync(candidate => candidate.Id == 2)).SuspendedReason);
            Assert.Equal("sense_removed", (await database.Cards.SingleAsync(candidate => candidate.Id == 3)).SuspendedReason);
        }
    }

    [Fact]
    public async Task ForecastCountsByLocalDateStartingTomorrow()
    {
        var clock = new LearnerClock(new FakeTimeProvider(Now), "Europe/Madrid");
        var tomorrow = clock.LocalToday.AddDays(1);
        var thirdDay = clock.LocalToday.AddDays(3);
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            for (var id = 1; id <= 4; id++)
            {
                AddWord(database, id, $"es-word-{id:D3}", $"palabra{id}");
            }
            // Overdue today: not part of the forward forecast.
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddDays(-1).ToUnixTimeMilliseconds());
            AddCard(database, 2, 2, state: CardState.Review,
                dueMs: clock.StartOfLocalDateUtc(tomorrow).AddHours(1).ToUnixTimeMilliseconds());
            AddCard(database, 3, 3, state: CardState.Learning,
                dueMs: clock.StartOfLocalDateUtc(tomorrow).AddHours(2).ToUnixTimeMilliseconds());
            AddCard(database, 4, 4, state: CardState.Review,
                dueMs: clock.StartOfLocalDateUtc(thirdDay).AddHours(1).ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        using var response = await client.GetAsync("/api/learn/forecast", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal(2, body.RootElement.GetProperty("tomorrow").GetInt32());
        var days = body.RootElement.GetProperty("next7Days").EnumerateArray().ToArray();
        Assert.Equal(7, days.Length);
        Assert.Equal(tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days[0].GetProperty("date").GetString());
        Assert.Equal(2, days[0].GetProperty("due").GetInt32());
        Assert.Equal(thirdDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days[2].GetProperty("date").GetString());
        Assert.Equal(1, days[2].GetProperty("due").GetInt32());
        Assert.Equal(0, days[1].GetProperty("due").GetInt32());
    }

    [Fact]
    public async Task WordsListsApprovedWordsWithStageAndFilters()
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela", article: "la", gender: "feminine", enGloss: "grandmother");
            AddWord(database, 2, "es-zapato-02", "zapato", article: "el", gender: "masculine", enGloss: "shoe");
            AddWord(database, 3, "es-perro-03", "perro", status: "pending_review", article: "el", enGloss: "dog");
            database.WordTopics.AddRange(new WordTopic { WordId = 1, Topic = "familia" },
                new WordTopic { WordId = 2, Topic = "ropa" });
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.AddDays(5).ToUnixTimeMilliseconds(), stability: 30);
            AddCard(database, 2, 1, direction: "en_es", state: CardState.New, dueMs: Now.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);

        using (var body = await JsonAsync(await client.GetAsync("/api/learn/words", CancellationToken.None)))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "abuela", "zapato" }, items.Select(item => item.GetProperty("headword").GetString()).ToArray());
            Assert.Equal("es-abuela-01", items[0].GetProperty("key").GetString());
            Assert.Equal("la", items[0].GetProperty("article").GetString());
            Assert.Equal("grandmother", items[0].GetProperty("gloss").GetString());
            Assert.Equal(new[] { "familia" },
                items[0].GetProperty("topics").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("consolidada", items[0].GetProperty("stage").GetString());
            Assert.Equal("nuevo", items[1].GetProperty("stage").GetString());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/learn/words?q=abuel", CancellationToken.None)))
        {
            Assert.Equal(new[] { "abuela" }, body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("headword").GetString()).ToArray());
        }
        using (var body = await JsonAsync(await client.GetAsync("/api/learn/words?topic=ropa", CancellationToken.None)))
        {
            Assert.Equal(new[] { "zapato" }, body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("headword").GetString()).ToArray());
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("rating")]
    [InlineData("guid")]
    [InlineData("mode")]
    [InlineData("reviewedAt")]
    [InlineData("too-many")]
    public async Task AttemptBatchesRejectUnusableItems(string kind)
    {
        using var factory = new LearnApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddCard(database, 1, 1, state: CardState.Review, dueMs: Now.ToUnixTimeMilliseconds());
        });
        using var client = await LearnerClientAsync(factory);
        var items = kind switch
        {
            "empty" => Array.Empty<object>(),
            "rating" => new object[] { Attempt(Guid.NewGuid().ToString(), 1, Now.AddMinutes(-5), rating: 9) },
            "guid" => new object[] { Attempt("not-a-guid", 1, Now.AddMinutes(-5)) },
            "mode" => new object[] { Attempt(Guid.NewGuid().ToString(), 1, Now.AddMinutes(-5), mode: "listening") },
            "reviewedAt" => new object[] { Attempt(Guid.NewGuid().ToString(), 1, null) },
            _ => Enumerable.Range(0, 51)
                .Select(_ => (object)Attempt(Guid.NewGuid().ToString(), 1, Now.AddMinutes(-5))).ToArray()
        };
        using var response = await PostJsonAsync(client, "/api/learn/attempts", new { attempts = items });
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");
        using var database = factory.OpenDatabase();
        Assert.Empty(await database.ReviewLogs.ToListAsync());
    }

    private static object Attempt(string clientAttemptId, int cardId, DateTimeOffset? reviewedAtUtc,
        int rating = 3, string mode = "recognition") => new
    {
        clientAttemptId, cardId, cardEpoch = 0, sessionId = "s", mode, rating,
        isCorrectMeaning = (bool?)null, isCorrectForm = (bool?)null, overridden = false,
        feedbackKind = (string?)null, answerText = (string?)null, durationMs = 800, reviewedAtUtc
    };

    private static ReviewLog Success(string clientAttemptId, int wordId, int cardId, long reviewedAtMs) => new()
    {
        ClientAttemptId = clientAttemptId, UserId = 1, WordId = wordId, CardId = cardId, CardEpoch = 0,
        Rating = Rating.Good, ReviewedAtMs = reviewedAtMs, ReceivedAtMs = reviewedAtMs,
        Mode = "recognition", CountsForSrs = true
    };

    private static void AddWord(PalabrasDbContext database, int id, string key, string headword,
        string status = "approved", string cefr = "A1", string pos = "noun", string? article = null,
        string? gender = null, string enGloss = "gloss", string esAccepted = "[]", string enAccepted = "[]",
        string exampleEs = "", string exampleEn = "")
    {
        database.Words.Add(new Word
        {
            Id = id, Key = key, Headword = headword, Article = article, Gender = gender, Pos = pos, Cefr = cefr,
            RegionScope = "ES", Register = "neutral", Status = status, ContentHash = "hash", ProvenanceMethod = "manual"
        });
        database.Senses.Add(new Sense
        {
            Id = id, WordId = id, SenseOrder = 0, EnGloss = enGloss, EnAccepted = enAccepted, EsAccepted = esAccepted,
            ExampleEs = exampleEs, ExampleEn = exampleEn
        });
        // S6b: the current version's content snapshot is the record of active sense ordinals, so
        // every realistic seed needs one. One sense row, one snapshot entry.
        SetSnapshot(database, id, 1);
    }

    private static void AddSense(PalabrasDbContext database, int senseId, int wordId, int senseOrder,
        string enGloss = "gloss", string exampleEs = "", string exampleEn = "")
    {
        database.Senses.Add(new Sense
        {
            Id = senseId, WordId = wordId, SenseOrder = senseOrder, EnGloss = enGloss,
            ExampleEs = exampleEs, ExampleEn = exampleEn
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
        string direction = "es_en", CardState state = CardState.New, long? dueMs = null, long? lastReviewMs = null,
        int epoch = 0, int reps = 0, int lapses = 0, string? suspended = null, bool leech = false,
        double? stability = null, string? introduced = null)
    {
        database.Cards.Add(new Card
        {
            Id = id, UserId = userId, SenseId = senseId, Direction = direction, State = state,
            DueMs = dueMs ?? Now.ToUnixTimeMilliseconds(), LastReviewMs = lastReviewMs, Epoch = epoch, Reps = reps,
            Lapses = lapses, SuspendedReason = suspended, IsLeech = leech, Stability = stability,
            IntroducedLocalDate = introduced, CreatedAt = Now.ToUnixTimeMilliseconds()
        });
    }

    private static async Task<HttpClient> LearnerClientAsync(LearnApplicationFactory factory, string username = "learner")
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

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Palabras", "1");
        return await client.SendAsync(request, CancellationToken.None);
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

    private sealed class LearnApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash("test-password");
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode",
            "palabras-learn-" + Guid.NewGuid().ToString("N"));
        private readonly FakeTimeProvider _time;
        private readonly IScheduler _scheduler;

        internal LearnApplicationFactory(DateTimeOffset now, Action<PalabrasDbContext>? seed = null,
            IScheduler? scheduler = null)
        {
            _time = new FakeTimeProvider(now);
            _scheduler = scheduler ?? new FakeScheduler();
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
                services.RemoveAll<IScheduler>();
                services.AddSingleton(_scheduler);
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
