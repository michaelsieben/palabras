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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Accounts;
using Palabras.Learn;
using Palabras.Persistence;
using Palabras.Srs;
using Xunit;

namespace Palabras.Tests;

// M7 contract tests: day status, the rolling-7-day skip and the streak
// (design-amendment.md ΔD:280-306, ΔF:631-640; design.md:171-179). Rows are seeded directly
// and every HTTP call goes through the real cookie auth and X-Palabras middleware against a
// migrated temp SQLite database, like M5LearnApiTests / M66StatsTests.
public sealed class M7DayStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T09:00:00Z", CultureInfo.InvariantCulture);
    private const string Today = "2026-10-18";

    [Fact]
    public async Task FreshDayHasNoStatusNoStreakAndASkipAvailable()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var body = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        var root = body.RootElement;
        Assert.Equal(Today, root.GetProperty("localDate").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("status").ValueKind);
        Assert.Equal(0, root.GetProperty("streak").GetInt32());
        Assert.True(root.GetProperty("skipAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("skipNextAvailable").ValueKind);
    }

    [Fact]
    public async Task SkipTodayWritesTheLedgerAndConsumesTheToken()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/skip",
            new { localDate = Today, source = "app" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(Today, root.GetProperty("localDate").GetString());
        Assert.Equal("skipped", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("skipAvailable").GetBoolean());
        // ΔD:283: the next allowed date is the consumed skip + 7.
        Assert.Equal("2026-10-25", root.GetProperty("skipNextAvailable").GetString());

        await using var database = factory.OpenDatabase();
        var skip = await database.DaySkips.SingleAsync(CancellationToken.None);
        Assert.Equal(Today, skip.LocalDate);
        Assert.Equal("app", skip.Source);
        Assert.Null(skip.RefundedAtMs);
        Assert.Equal(Today, (await database.DayStatuses.SingleAsync(CancellationToken.None)).LocalDate);
        Assert.Equal("skipped", (await database.DayStatuses.SingleAsync(CancellationToken.None)).Status);

        using var status = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.Equal("skipped", status.RootElement.GetProperty("status").GetString());
        Assert.False(status.RootElement.GetProperty("skipAvailable").GetBoolean());
    }

    [Fact]
    public async Task SkipIsAllowedFromTheNotificationSourceToo()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/skip",
            new { localDate = Today, source = "notification" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = factory.OpenDatabase();
        Assert.Equal("notification", (await database.DaySkips.SingleAsync(CancellationToken.None)).Source);
    }

    [Fact]
    public async Task SecondSkipOnTheSameDayIsIdempotent()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var first = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        using var second = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var body = await JsonAsync(second);
        Assert.Equal("skipped", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("2026-10-25", body.RootElement.GetProperty("skipNextAvailable").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Equal(1, await database.DaySkips.CountAsync(CancellationToken.None));
        Assert.Equal(1, await database.DayStatuses.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SecondSkipInsideTheSevenDayWindowIsRejected()
    {
        using var factory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-17", "skipped");
            AddSkip(database, "2026-10-17");
        });
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("skip_unavailable", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal("2026-10-24", body.RootElement.GetProperty("nextAvailable").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Equal(1, await database.DaySkips.CountAsync(CancellationToken.None));
    }

    [Theory]
    // [today - 6, today] blocks; today - 7 has left the window (ΔD:282).
    [InlineData("2026-10-12", HttpStatusCode.Conflict, "2026-10-19")]
    [InlineData("2026-10-11", HttpStatusCode.OK, null)]
    public async Task SkipWindowBoundaryIsInclusiveOnDaySixAndOpenOnDaySeven(string previousSkip,
        HttpStatusCode expected, string? nextAvailable)
    {
        using var factory = new DayApplicationFactory(Now, database => AddSkip(database, previousSkip));
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        Assert.Equal(expected, response.StatusCode);
        if (nextAvailable is null)
        {
            return;
        }
        using var body = await JsonAsync(response);
        Assert.Equal("skip_unavailable", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal(nextAvailable, body.RootElement.GetProperty("nextAvailable").GetString());
    }

    [Fact]
    public async Task CompletingTheDayRefundsATodaySkipAndTheTokenComesBack()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using (var skip = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" }))
        {
            Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        }
        using var done = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 640, plannedS = 600, newCount = 4, reviewCount = 21 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using var body = await JsonAsync(done);
        var root = body.RootElement;
        Assert.Equal("done", root.GetProperty("status").GetString());
        // ΔD:293: the same-day skip is refunded, so the token is available again.
        Assert.True(root.GetProperty("skipAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("skipNextAvailable").ValueKind);

        await using var database = factory.OpenDatabase();
        var skipRow = await database.DaySkips.SingleAsync(CancellationToken.None);
        Assert.NotNull(skipRow.RefundedAtMs);
        var day = await database.DayStatuses.SingleAsync(CancellationToken.None);
        Assert.Equal("done", day.Status);
        var session = await database.PracticeSessions.SingleAsync(CancellationToken.None);
        Assert.Equal("completed", session.Status);
        Assert.Equal(640, session.DurationS);
        Assert.Equal(4, session.NewCount);
        Assert.Equal(21, session.ReviewCount);
        Assert.Equal(session.Id, day.SessionId);
    }

    [Fact]
    public async Task SkipOnAnAlreadyDoneDayIsRejected()
    {
        using var factory = new DayApplicationFactory(Now, database => AddDay(database, Today, "done"));
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("already_done", body.RootElement.GetProperty("reason").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.DaySkips.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompletingTheDayTwiceWritesOneSession()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var first = await PostJsonAsync(client, "/api/learn/day/done", new { durationS = 600 });
        using var second = await PostJsonAsync(client, "/api/learn/day/done", new { durationS = 700 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var body = await JsonAsync(second);
        Assert.Equal("done", body.RootElement.GetProperty("status").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Equal(1, await database.PracticeSessions.CountAsync(CancellationToken.None));
        Assert.Equal(600, (await database.PracticeSessions.SingleAsync(CancellationToken.None)).DurationS);
    }

    [Theory]
    [InlineData("""{"localDate":"2026-10-17","source":"app"}""")]
    [InlineData("""{"localDate":"18/10/2026","source":"app"}""")]
    [InlineData("""{"source":"app"}""")]
    [InlineData("""{"localDate":"2026-10-18","source":"web"}""")]
    [InlineData("""{"localDate":"2026-10-18"}""")]
    public async Task SkipRejectsAMalformedBodyOrADateThatIsNotTodayInTheLearnerZone(string json)
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/learn/day/skip")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.DaySkips.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EmptyAndNullSkipBodiesAreRejected()
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        foreach (var json in new[] { "", "null" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/learn/day/skip")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Palabras", "1");
            using var response = await client.SendAsync(request, CancellationToken.None);
            await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");
        }
    }

    [Theory]
    // O-M7-9 (C5): a named target must parse exactly and be today or yesterday in the learner
    // zone. Two days back, tomorrow, a wrong format and an empty string are all refused, and
    // the refusal never silently completes the receipt day.
    [InlineData("2026-10-16")]
    [InlineData("2026-10-19")]
    [InlineData("18/10/2026")]
    [InlineData("2026-10-18T00:00")]
    [InlineData("")]
    public async Task DoneOutsideTheBoundedLateDeliveryWindowIsRejected(string localDate)
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate });
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.PracticeSessions.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ADoneBeforeMidnightFlushedAfterMidnightCompletesTheDayItNames()
    {
        // 23:00Z is 01:00 local on 2026-10-18 in Madrid: the receipt day is the 18th, the
        // session completed on the 17th. Persistence, dedupe and the response follow the
        // named target day, not the receipt day (C5).
        var now = DateTimeOffset.Parse("2026-10-17T23:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now);
        using var client = await LearnerClientAsync(factory);

        using var done = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 620, localDate = "2026-10-17" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using var body = await JsonAsync(done);
        Assert.Equal("2026-10-17", body.RootElement.GetProperty("localDate").GetString());
        Assert.Equal("done", body.RootElement.GetProperty("status").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Equal("2026-10-17", (await database.DayStatuses.SingleAsync(CancellationToken.None)).LocalDate);
        Assert.Equal("2026-10-17", (await database.PracticeSessions.SingleAsync(CancellationToken.None)).LocalDate);
        // The receipt day was not silently completed: today (2026-10-18) still has no row.
        using var status = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.Equal("2026-10-18", status.RootElement.GetProperty("localDate").GetString());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task ARetryAfterALostResponseRepeatsTheLateDoneWithoutDuplicates()
    {
        // The first response never arrived; the retry is delivered later the same receipt day.
        // It is a no-op on the target day: one session row, one day row, same payload.
        var now = DateTimeOffset.Parse("2026-10-17T23:30:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now);
        using var client = await LearnerClientAsync(factory);

        using var first = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-17" });
        using var second = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 700, localDate = "2026-10-17" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var body = await JsonAsync(second);
        Assert.Equal("2026-10-17", body.RootElement.GetProperty("localDate").GetString());
        Assert.Equal("done", body.RootElement.GetProperty("status").GetString());

        await using var database = factory.OpenDatabase();
        Assert.Equal(1, await database.DayStatuses.CountAsync(CancellationToken.None));
        var session = await database.PracticeSessions.SingleAsync(CancellationToken.None);
        Assert.Equal(600, session.DurationS);
    }

    [Fact]
    public async Task ARetryAfterTheWindowRejectsInsteadOfCompletingANewDay()
    {
        // C5: the flush of the 17th arrives two receipt days late. The only accepted outcome
        // is a refusal; nothing is written for any day, and certainly not for today.
        using var factory = new DayApplicationFactory(Now); // today = 2026-10-18
        using var client = await LearnerClientAsync(factory);

        using var late = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-16" });
        await AssertJsonError(late, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.PracticeSessions.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ADoneAgainstARolloverMissedRowOverwritesTheTargetDay()
    {
        // Completion versus rollover: the 00:05 tick already labelled the 17th missed. The
        // late completion is the stronger fact (O-M7-7 applied to the named target day).
        var now = DateTimeOffset.Parse("2026-10-17T23:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now, database =>
            AddDay(database, "2026-10-17", "missed"));
        using var client = await LearnerClientAsync(factory);

        using var done = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 610, localDate = "2026-10-17" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        await using var database = factory.OpenDatabase();
        var day = await database.DayStatuses.SingleAsync(CancellationToken.None);
        Assert.Equal("done", day.Status);
        Assert.Equal("2026-10-17", day.LocalDate);
        var session = await database.PracticeSessions.SingleAsync(CancellationToken.None);
        Assert.Equal(day.SessionId, session.Id);
    }

    [Fact]
    public async Task ALateDoneRefundsTheSkipOfTheDayItNames()
    {
        // ΔD:293 via C5: skipped the 17th, completed a session the 17th offline, the flush
        // lands on the 18th. The refund moves with the target date, not the receipt date,
        // and the refunded token leaves today's skip available again.
        var now = DateTimeOffset.Parse("2026-10-17T23:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now, database =>
        {
            AddDay(database, "2026-10-17", "skipped");
            AddSkip(database, "2026-10-17");
        });
        using var client = await LearnerClientAsync(factory);

        using var done = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 540, localDate = "2026-10-17" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        await using var database = factory.OpenDatabase();
        Assert.NotNull((await database.DaySkips.SingleAsync(CancellationToken.None)).RefundedAtMs);
        Assert.Equal("done", (await database.DayStatuses.SingleAsync(CancellationToken.None)).Status);
        using var status = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.True(status.RootElement.GetProperty("skipAvailable").GetBoolean());
    }

    [Fact]
    public async Task TheLearnerZoneDecidesWhichLocalDatesCanBeCompleted()
    {
        // 02:00Z is 22:00 on 2026-10-17 in New York (UTC-4) but 04:00 on 2026-10-18 in Madrid.
        // The bounded window is the New Yorker's today and yesterday; the Madrid calendar date
        // is a future day there and must be refused, never silently completed.
        var now = DateTimeOffset.Parse("2026-10-18T02:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now, timezone: "America/New_York");
        using var client = await LearnerClientAsync(factory);

        using var yesterday = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-16" });
        Assert.Equal(HttpStatusCode.OK, yesterday.StatusCode);
        using var today = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-17" });
        Assert.Equal(HttpStatusCode.OK, today.StatusCode);
        using var body = await JsonAsync(today);
        Assert.Equal("2026-10-17", body.RootElement.GetProperty("localDate").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("streak").GetInt32());

        using var future = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-18" });
        await AssertJsonError(future, HttpStatusCode.BadRequest, "bad_request");
        using var tooOld = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-15" });
        await AssertJsonError(tooOld, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        var dates = (await database.DayStatuses.ToListAsync(CancellationToken.None))
            .Select(row => row.LocalDate).OrderBy(date => date, StringComparer.Ordinal).ToArray();
        Assert.Equal(["2026-10-16", "2026-10-17"], dates);
    }

    [Fact]
    public async Task ADoneBeforeTheAccountExistedIsRejected()
    {
        // The bounded window alone could still name yesterday of an account created today.
        // A completion cannot predate the account (O-M7-8 applied to the done path too).
        using var factory = new DayApplicationFactory(Now,
            createdAt: DateTimeOffset.Parse("2026-10-18T05:00:00Z", CultureInfo.InvariantCulture));
        using var client = await LearnerClientAsync(factory);

        using var response = await PostJsonAsync(client, "/api/learn/day/done",
            new { durationS = 600, localDate = "2026-10-17" });
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DoneWithoutADateStillCompletesTheReceiptDay()
    {
        // Old-caller compatibility: the pre-M7-9 body (no localDate) keeps marking today done.
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var done = await PostJsonAsync(client, "/api/learn/day/done", new { durationS = 600 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using var body = await JsonAsync(done);
        Assert.Equal(Today, body.RootElement.GetProperty("localDate").GetString());
        Assert.Equal("done", body.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"durationS":-1}""")]
    [InlineData("""{"plannedS":-5}""")]
    [InlineData("""{"newCount":-1}""")]
    [InlineData("""{"reviewCount":-2}""")]
    public async Task DoneRejectsNegativeCounts(string json)
    {
        using var factory = new DayApplicationFactory(Now);
        using var client = await LearnerClientAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/learn/day/done")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Palabras", "1");
        using var response = await client.SendAsync(request, CancellationToken.None);
        await AssertJsonError(response, HttpStatusCode.BadRequest, "bad_request");

        await using var database = factory.OpenDatabase();
        Assert.Empty(await database.PracticeSessions.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DayRoutesRejectAnonymousReviewerAndHeaderlessWrites()
    {
        using var factory = new DayApplicationFactory(Now);
        using var anonymous = factory.CreateHttpClient();
        using (var unauthorized = await anonymous.GetAsync("/api/learn/day/status", CancellationToken.None))
        {
            await AssertJsonError(unauthorized, HttpStatusCode.Unauthorized, "unauthorized");
        }
        using (var unauthorized = await anonymous.PostAsJsonAsync("/api/learn/day/skip",
                   new { localDate = Today, source = "app" }, CancellationToken.None))
        {
            // No cookie and no X-Palabras: the CSRF middleware answers first.
            await AssertJsonError(unauthorized, HttpStatusCode.BadRequest, "missing_x_palabras");
        }

        using var reviewer = await LearnerClientAsync(factory, "reviewer");
        foreach (var path in new[] { "/api/learn/day/status", "/api/learn/day/done" })
        {
            using var response = path.EndsWith("status", StringComparison.Ordinal)
                ? await reviewer.GetAsync(path, CancellationToken.None)
                : await PostJsonAsync(reviewer, path, new { durationS = 600 });
            await AssertJsonError(response, HttpStatusCode.Forbidden, "forbidden");
        }

        using var owner = await LearnerClientAsync(factory);
        using var headerless = new HttpRequestMessage(HttpMethod.Post, "/api/learn/day/skip")
        {
            Content = JsonContent.Create(new { localDate = Today, source = "app" })
        };
        using var missing = await owner.SendAsync(headerless, CancellationToken.None);
        await AssertJsonError(missing, HttpStatusCode.BadRequest, "missing_x_palabras");
    }

    [Fact]
    public async Task AnotherLearnersDayRowsAreInvisible()
    {
        using var factory = new DayApplicationFactory(Now, database =>
        {
            // The schema permits one owner and one reviewer (PalabrasDbContext.cs), so the second
            // learner's history is seeded under user 2 and read back through user 1's cookie.
            AddDay(database, "2026-10-17", "skipped", userId: 2);
            AddSkip(database, "2026-10-17", userId: 2);
            AddDay(database, Today, "done", userId: 2);
        });
        using var client = await LearnerClientAsync(factory);

        using var body = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        var root = body.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("status").ValueKind);
        Assert.Equal(0, root.GetProperty("streak").GetInt32());
        Assert.True(root.GetProperty("skipAvailable").GetBoolean());

        using var skip = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = Today, source = "app" });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        await using var database = factory.OpenDatabase();
        Assert.Equal(2, await database.DaySkips.CountAsync(CancellationToken.None));
        // The seeded rows stay: two for user 2 plus today's skipped row for user 1.
        Assert.Equal(3, await database.DayStatuses.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StreakCountsDoneDaysAndOnlyLetsASkipCarryTheChain()
    {
        using var factory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-15", "done");
            AddDay(database, "2026-10-16", "done");
            AddDay(database, "2026-10-17", "done");
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        // Today has no row yet; the chain earned up to yesterday is the headline (ΔC.3).
        Assert.Equal(3, body.RootElement.GetProperty("streak").GetInt32());

        using var skippedFactory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-15", "done");
            AddDay(database, "2026-10-16", "done");
            AddDay(database, "2026-10-17", "skipped");
            AddSkip(database, "2026-10-17");
        });
        using var skippedClient = await LearnerClientAsync(skippedFactory);
        using var skipped = await JsonAsync(await skippedClient.GetAsync("/api/learn/day/status", CancellationToken.None));
        // ΔD:290: the skip preserves the chain but does not increment it.
        Assert.Equal(2, skipped.RootElement.GetProperty("streak").GetInt32());

        using var missedFactory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-15", "done");
            AddDay(database, "2026-10-16", "done");
            AddDay(database, "2026-10-17", "missed");
        });
        using var missedClient = await LearnerClientAsync(missedFactory);
        using var missed = await JsonAsync(await missedClient.GetAsync("/api/learn/day/status", CancellationToken.None));
        // A plain missed day ends the chain (ΔD:290).
        Assert.Equal(0, missed.RootElement.GetProperty("streak").GetInt32());
    }

    [Fact]
    public async Task DoneTodayIncrementsTheStreakAndASkippedDayDoesNot()
    {
        using var doneFactory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-16", "done");
            AddDay(database, "2026-10-17", "done");
        });
        using var doneClient = await LearnerClientAsync(doneFactory);
        using (var done = await PostJsonAsync(doneClient, "/api/learn/day/done", new { durationS = 600 }))
        {
            using var body = await JsonAsync(done);
            Assert.Equal(3, body.RootElement.GetProperty("streak").GetInt32());
        }

        using var skippedFactory = new DayApplicationFactory(Now, database =>
        {
            AddDay(database, "2026-10-16", "done");
            AddDay(database, "2026-10-17", "done");
        });
        using var skippedClient = await LearnerClientAsync(skippedFactory);
        using (var skip = await PostJsonAsync(skippedClient, "/api/learn/day/skip", new { localDate = Today, source = "app" }))
        {
            using var body = await JsonAsync(skip);
            Assert.Equal(2, body.RootElement.GetProperty("streak").GetInt32());
        }
    }

    [Fact]
    public async Task TodayWithoutARowKeepsYesterdaysStreakButAnEmptyYesterdayBreaksIt()
    {
        using var factory = new DayApplicationFactory(Now, database =>
        {
            // Today absent, yesterday done: the streak up to yesterday stands (22:00 reminder).
            AddDay(database, "2026-10-17", "done");
        });
        using var client = await LearnerClientAsync(factory);
        using var body = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.Equal(1, body.RootElement.GetProperty("streak").GetInt32());

        using var gapFactory = new DayApplicationFactory(Now, database => AddDay(database, "2026-10-16", "done"));
        using var gapClient = await LearnerClientAsync(gapFactory);
        using var gap = await JsonAsync(await gapClient.GetAsync("/api/learn/day/status", CancellationToken.None));
        // Nothing on 2026-10-17 and nothing today: no chain.
        Assert.Equal(0, gap.RootElement.GetProperty("streak").GetInt32());
    }

    [Fact]
    public async Task SkipAndStreakSurviveTheAutumnDstDay()
    {
        // Europe/Madrid 2026-10-25 is a 25-hour day; window arithmetic is on local dates, so the
        // skip from 2026-10-18 (today - 7) must be outside the window on 2026-10-25.
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now, database =>
        {
            AddSkip(database, "2026-10-18");
            AddDay(database, "2026-10-19", "skipped");
            AddDay(database, "2026-10-23", "done");
            AddDay(database, "2026-10-24", "done");
        });
        using var client = await LearnerClientAsync(factory);

        using var status = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.Equal("2026-10-25", status.RootElement.GetProperty("localDate").GetString());
        Assert.Equal(2, status.RootElement.GetProperty("streak").GetInt32());
        Assert.True(status.RootElement.GetProperty("skipAvailable").GetBoolean());

        using var skip = await PostJsonAsync(client, "/api/learn/day/skip", new { localDate = "2026-10-25", source = "app" });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        using var body = await JsonAsync(skip);
        Assert.Equal("2026-11-01", body.RootElement.GetProperty("skipNextAvailable").GetString());
    }

    [Fact]
    public async Task TheLearnerZoneDecidesWhichLocalDateCanBeSkipped()
    {
        // 02:00 UTC is 22:00 on 2026-10-17 in New York (UTC-4) but 04:00 on 2026-10-18 in Madrid.
        var now = DateTimeOffset.Parse("2026-10-18T02:00:00Z", CultureInfo.InvariantCulture);
        using var factory = new DayApplicationFactory(now, timezone: "America/New_York");
        using var client = await LearnerClientAsync(factory);

        using var status = await JsonAsync(await client.GetAsync("/api/learn/day/status", CancellationToken.None));
        Assert.Equal("2026-10-17", status.RootElement.GetProperty("localDate").GetString());

        using var madridDate = await PostJsonAsync(client, "/api/learn/day/skip",
            new { localDate = "2026-10-18", source = "app" });
        await AssertJsonError(madridDate, HttpStatusCode.BadRequest, "bad_request");

        using var localDate = await PostJsonAsync(client, "/api/learn/day/skip",
            new { localDate = "2026-10-17", source = "app" });
        Assert.Equal(HttpStatusCode.OK, localDate.StatusCode);
        using var body = await JsonAsync(localDate);
        Assert.Equal("2026-10-17", body.RootElement.GetProperty("localDate").GetString());
    }

    [Fact]
    public async Task RolloverWritesMissedForYesterdayAndOnlyThen()
    {
        using var rig = new DatabaseRig();
        // 00:04 local Madrid: the rollover window has not opened yet.
        Assert.Equal(0, await DayRollover.RunAsync(rig.Settings, new FakeTimeProvider(
            DateTimeOffset.Parse("2026-10-17T22:04:00Z", CultureInfo.InvariantCulture)),
            NullLogger.Instance, DateTimeOffset.UnixEpoch, CancellationToken.None));
        await using (var database = rig.Settings.CreateContext())
        {
            Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
        }

        // 00:10 local Madrid: yesterday (2026-10-17) has no done/skipped row, so it is missed.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T22:10:00Z", CultureInfo.InvariantCulture));
        Assert.Equal(1, await DayRollover.RunAsync(rig.Settings, time, NullLogger.Instance,
            DateTimeOffset.UnixEpoch, CancellationToken.None));
        // Idempotent: the minute tick runs again and writes nothing.
        Assert.Equal(0, await DayRollover.RunAsync(rig.Settings, time, NullLogger.Instance,
            DateTimeOffset.UnixEpoch, CancellationToken.None));
        await using (var database = rig.Settings.CreateContext())
        {
            var day = await database.DayStatuses.SingleAsync(CancellationToken.None);
            Assert.Equal("2026-10-17", day.LocalDate);
            Assert.Equal("missed", day.Status);
            Assert.Null(day.SessionId);
        }
    }

    [Fact]
    public async Task RolloverNeverOverwritesADoneOrSkippedDayAndNeverInventsPreAccountHistory()
    {
        using var rig = new DatabaseRig();
        await using (var database = rig.Settings.CreateContext())
        {
            database.DayStatuses.Add(new DayStatus
            {
                UserId = 1, LocalDate = "2026-10-17", Status = "done", DecidedAtMs = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds()
            });
            await database.SaveChangesAsync(CancellationToken.None);
        }
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T22:10:00Z", CultureInfo.InvariantCulture));
        Assert.Equal(0, await DayRollover.RunAsync(rig.Settings, time, NullLogger.Instance,
            DateTimeOffset.UnixEpoch, CancellationToken.None));
        await using (var database = rig.Settings.CreateContext())
        {
            Assert.Equal("done", (await database.DayStatuses.SingleAsync(CancellationToken.None)).Status);
        }

        // The account was created at 00:20 local today, ten minutes after the rollover tick, so
        // yesterday is a day before the account existed and must not become a missed day.
        using var freshRig = new DatabaseRig(createdAt: DateTimeOffset.Parse("2026-10-18T22:20:00Z",
            CultureInfo.InvariantCulture));
        var later = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T22:10:00Z", CultureInfo.InvariantCulture));
        Assert.Equal(0, await DayRollover.RunAsync(freshRig.Settings, later, NullLogger.Instance,
            DateTimeOffset.UnixEpoch, CancellationToken.None));
        await using (var database = freshRig.Settings.CreateContext())
        {
            Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
        }
    }

    [Theory]
    [MemberData(nameof(StreakCases))]
    public void StreakIsTheTrailingChainOfDoneDaysWithSkipsCarryingIt(string[] trailingStatuses, int expected)
    {
        var today = new DateOnly(2026, 10, 18);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < trailingStatuses.Length; index++)
        {
            // Oldest first; today is the last element.
            var date = today.AddDays(-(trailingStatuses.Length - 1) + index);
            statuses[LearnDayStatus.Key(date)] = trailingStatuses[index];
        }
        Assert.Equal(expected, LearnDayStatus.Streak(statuses, today));
    }

    public static IEnumerable<object[]> StreakCases() =>
    [
        // Chain: done, done, skipped, done. A skip carries the chain but adds nothing (ΔD:290).
        [new[] { "done", "done", "skipped", "done" }, 3],
        [new[] { "done", "missed", "done" }, 1],
        [new[] { "skipped", "skipped" }, 0],
        [new[] { "done" }, 1],
        [Array.Empty<string>(), 0]
    ];

    [Fact]
    public void SkipAvailabilityUsesTheRollingWindowAndIgnoresFutureRows()
    {
        var today = new DateOnly(2026, 10, 18);
        Assert.True(LearnDayStatus.EvaluateSkip([], today).Available);
        var consumed = LearnDayStatus.EvaluateSkip([today.ToString("yyyy-MM-dd")], today);
        Assert.False(consumed.Available);
        Assert.Equal("2026-10-25", consumed.NextAvailable);
        var edge = LearnDayStatus.EvaluateSkip([today.AddDays(-6).ToString("yyyy-MM-dd")], today);
        Assert.False(edge.Available);
        Assert.Equal("2026-10-19", edge.NextAvailable);
        Assert.True(LearnDayStatus.EvaluateSkip([today.AddDays(-7).ToString("yyyy-MM-dd")], today).Available);
        Assert.True(LearnDayStatus.EvaluateSkip([today.AddDays(1).ToString("yyyy-MM-dd")], today).Available);
    }

    private static void AddDay(PalabrasDbContext database, string localDate, string status, int userId = 1) =>
        database.DayStatuses.Add(new DayStatus
        {
            UserId = userId,
            LocalDate = localDate,
            Status = status,
            DecidedAtMs = Now.ToUnixTimeMilliseconds()
        });

    private static void AddSkip(PalabrasDbContext database, string localDate, int userId = 1,
        long? refundedAtMs = null) =>
        database.DaySkips.Add(new DaySkip
        {
            UserId = userId,
            LocalDate = localDate,
            Source = "app",
            CreatedAtMs = Now.ToUnixTimeMilliseconds(),
            RefundedAtMs = refundedAtMs
        });

    private static async Task<HttpClient> LearnerClientAsync(DayApplicationFactory factory, string username = "learner")
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

    // Bare migrated database for the rollover tests: no web host, no auth, one owner account.
    private sealed class DatabaseRig : IDisposable
    {
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode",
            "palabras-day-" + Guid.NewGuid().ToString("N"));

        internal DatabaseRig(DateTimeOffset? createdAt = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            Settings = new DatabaseSettings(DatabasePath);
            using var database = Settings.CreateContext();
            database.Database.Migrate();
            var stamp = createdAt ?? DateTimeOffset.UnixEpoch;
            database.Users.Add(new User
            {
                Id = 1, Username = "owner", Role = "owner", PasswordHash = "hash", CreatedAt = stamp, UpdatedAt = stamp
            });
            database.SaveChanges();
        }

        internal DatabaseSettings Settings { get; }

        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class DayApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string PasswordHash = new AccountPasswordHasher().Hash("test-password");
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode",
            "palabras-day-" + Guid.NewGuid().ToString("N"));
        private readonly FakeTimeProvider _time;

        internal DayApplicationFactory(DateTimeOffset now, Action<PalabrasDbContext>? seed = null,
            string? timezone = null, DateTimeOffset? createdAt = null)
        {
            _time = new FakeTimeProvider(now);
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var database = new DatabaseSettings(DatabasePath).CreateContext();
            database.Database.Migrate();
            var stamp = createdAt ?? DateTimeOffset.UnixEpoch;
            database.Users.AddRange(
                new User
                {
                    Id = 1, Username = "learner", Role = "owner", PasswordHash = PasswordHash,
                    CreatedAt = stamp, UpdatedAt = stamp, Timezone = timezone
                },
                new User
                {
                    Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = PasswordHash,
                    CreatedAt = stamp, UpdatedAt = stamp
                });
            database.SaveChanges();
            seed?.Invoke(database);
            database.SaveChanges();
        }

        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

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
