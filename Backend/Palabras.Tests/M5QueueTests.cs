using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Palabras.Persistence;
using Palabras.Srs;
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

public sealed class M5QueueTests
{
    [Fact]
    public void MadridTimeZoneSmokeTest()
    {
        Assert.Equal("Europe/Madrid", TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid").Id);
    }

    [Theory]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25", "2026-10-25T22:59:59.999Z", 25)]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25", "2026-10-25T22:59:59.999Z", 25)]
    [InlineData("2027-03-28T00:30:00Z", "2027-03-28", "2027-03-28T21:59:59.999Z", 23)]
    [InlineData("2027-03-28T01:30:00Z", "2027-03-28", "2027-03-28T21:59:59.999Z", 23)]
    public void DstDayUsesLocalMidnightNotTwentyFourHours(string now, string today, string end, int dayHours)
    {
        var provider = new FakeTimeProvider(Parse(now));
        var clock = new LearnerClock(provider, "Europe/Madrid");
        Assert.Equal(DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture), clock.LocalToday);
        Assert.Equal(Parse(end), clock.EndOfLocalDayUtc);
        DateTimeOffset start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(clock.LocalToday.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid")));
        Assert.Equal(TimeSpan.FromHours(dayHours), clock.EndOfLocalDayUtc.AddMilliseconds(1) - start);
        Assert.Equal(clock.LocalToday, clock.LocalDateOf(clock.EndOfLocalDayUtc));
        Assert.Equal(clock.LocalToday.AddDays(1), clock.LocalDateOf(clock.EndOfLocalDayUtc.AddMilliseconds(1)));
    }

    [Theory]
    [InlineData("2026-10-24T21:59:59.999Z", "2026-10-24", "2026-10-25")]
    [InlineData("2026-10-25T22:59:59.999Z", "2026-10-25", "2026-10-26")]
    [InlineData("2027-03-27T22:59:59.999Z", "2027-03-27", "2027-03-28")]
    [InlineData("2027-03-28T21:59:59.999Z", "2027-03-28", "2027-03-29")]
    public void ClockAdvancesAtLocalMidnight(string before, string today, string tomorrow)
    {
        var provider = new FakeTimeProvider(Parse(before));
        var clock = new LearnerClock(provider, null);
        Assert.Equal(DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture), clock.LocalToday);
        Assert.Equal(Parse(before), clock.EndOfLocalDayUtc);
        provider.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(DateOnly.ParseExact(tomorrow, "yyyy-MM-dd", CultureInfo.InvariantCulture), clock.LocalToday);
    }

    [Fact]
    public void UserZoneOverridesMadridFallbackAndInvalidZoneFailsExplicitly()
    {
        var provider = new FakeTimeProvider(Parse("2026-10-24T23:30:00Z"));
        Assert.Equal(new DateOnly(2026, 10, 25), new LearnerClock(provider, null).LocalToday);
        Assert.Equal(new DateOnly(2026, 10, 24), new LearnerClock(provider, "America/New_York").LocalToday);
        Assert.Throws<TimeZoneNotFoundException>(() => new LearnerClock(provider, "not/a-zone"));
    }

    [Theory]
    [InlineData(-25, -24)]
    [InlineData(-24, -24)]
    [InlineData(-1, -1)]
    [InlineData(1, 0.08333333333333333)]
    public void ReviewedAtClampsAgainstProviderTime(double inputHours, double expectedHours)
    {
        DateTimeOffset now = Parse("2026-10-25T01:30:00Z");
        var clock = new LearnerClock(new FakeTimeProvider(now), "Europe/Madrid");
        Assert.Equal(now.AddHours(expectedHours), clock.ClampReviewedAt(now.AddHours(inputHours)));
        Assert.Equal(now.AddMinutes(5), clock.ClampReviewedAt(now.AddMinutes(5)));
        Assert.Equal(now, clock.ClampReviewedAt(now.ToOffset(TimeSpan.FromHours(2))));
        Assert.Equal(TimeSpan.Zero, clock.ClampReviewedAt(now.ToOffset(TimeSpan.FromHours(2))).Offset);
    }

    [Theory]
    [InlineData("2026-10-25T01:30:00Z")]
    [InlineData("2027-03-28T01:30:00Z")]
    public async Task DueAndNewQueriesTranslateEntirelyToSqliteSqlAsync(string now)
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        var clock = new LearnerClock(new FakeTimeProvider(Parse(now)), "Europe/Madrid");
        string today = clock.LocalToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        long endMs = clock.EndOfLocalDayUtc.ToUnixTimeMilliseconds();
        long startMs = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(clock.LocalToday.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"))).ToUnixTimeMilliseconds();
        database.Users.AddRange(new User { Id = 1, Username = "owner", Role = "owner", PasswordHash = "hash" }, new User { Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = "hash" });
        for (int id = 1; id <= 15; id++)
        {
            database.Words.Add(new Word { Id = id, Key = $"word-{id}", Headword = "coger", Pos = "verb", Cefr = id == 11 ? "A2" : "A1", RegionScope = "ES", Register = "neutral", ContentHash = "hash", ProvenanceMethod = "manual", Status = id == 5 ? "pending_review" : "approved" });
            database.Senses.Add(new Sense { Id = id, WordId = id, SenseOrder = 1 });
        }
        database.Cards.AddRange(
            new Card { Id = 1, UserId = 1, SenseId = 1, State = CardState.Review, DueMs = endMs },
            new Card { Id = 2, UserId = 1, SenseId = 2, State = CardState.Learning, DueMs = endMs - 10 },
            new Card { Id = 3, UserId = 1, SenseId = 3, State = CardState.Relearning, DueMs = endMs - 20 },
            new Card { Id = 4, UserId = 2, SenseId = 4, State = CardState.Review, DueMs = 0 },
            new Card { Id = 5, UserId = 1, SenseId = 5, State = CardState.Review, DueMs = 0 },
            new Card { Id = 6, UserId = 1, SenseId = 6, State = CardState.Review, DueMs = 0, SuspendedReason = "manual" },
            new Card { Id = 7, UserId = 1, SenseId = 7, State = CardState.Review, DueMs = 0, BuriedUntilLocalDate = today },
            new Card { Id = 8, UserId = 1, SenseId = 8, State = CardState.Review, DueMs = endMs + 1 },
            new Card { Id = 9, UserId = 1, SenseId = 9, State = CardState.Review, DueMs = endMs - 30, Direction = "en_es" },
            new Card { Id = 10, UserId = 1, SenseId = 9, State = CardState.Review, DueMs = endMs + 1 },
            new Card { Id = 11, UserId = 1, SenseId = 10, State = CardState.Review, DueMs = 0, Direction = "en_es" },
            new Card { Id = 12, UserId = 1, SenseId = 10, State = CardState.Review, DueMs = endMs + 1 },
            new Card { Id = 13, UserId = 1, SenseId = 11, State = CardState.New },
            new Card { Id = 14, UserId = 1, SenseId = 12, State = CardState.New, BuriedUntilLocalDate = clock.LocalToday.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            new Card { Id = 15, UserId = 1, SenseId = 13, State = CardState.New, Direction = "en_es" },
            new Card { Id = 16, UserId = 1, SenseId = 14, State = CardState.New, SuspendedReason = "leech" },
            new Card { Id = 17, UserId = 1, SenseId = 15, State = CardState.New, BuriedUntilLocalDate = today });
        database.ReviewLogs.AddRange(
            new ReviewLog { UserId = 1, WordId = 9, CardId = 10, Rating = Rating.Good, ClientAttemptId = "prior-success", ReviewedAtMs = startMs - 1, CountsForSrs = true },
            new ReviewLog { UserId = 1, WordId = 10, CardId = 12, Rating = Rating.Good, ClientAttemptId = "today-success", ReviewedAtMs = startMs, CountsForSrs = true },
            new ReviewLog { UserId = 1, WordId = 10, CardId = 12, Rating = Rating.Good, ClientAttemptId = "prior-ignored", ReviewedAtMs = startMs - 1, CountsForSrs = false },
            new ReviewLog { UserId = 1, WordId = 10, CardId = 12, Rating = Rating.Good, ClientAttemptId = "prior-epoch", ReviewedAtMs = startMs - 1, CountsForSrs = true, CardEpoch = 1 },
            new ReviewLog { UserId = 1, WordId = 10, CardId = 12, Rating = Rating.Again, ClientAttemptId = "prior-failure", ReviewedAtMs = startMs - 1, CountsForSrs = true });
        await database.SaveChangesAsync(CancellationToken.None);

        // No AsEnumerable/ToList before filters/order/limit: IQueryable plus ToQueryString requires
        // every queue predicate to translate. EF throws for untranslatable Where/OrderBy in EF 10.
        var eligible = from card in database.Cards.AsNoTracking()
                       join sense in database.Senses on card.SenseId equals sense.Id
                       join word in database.Words on sense.WordId equals word.Id
                       where card.UserId == 1 && card.SuspendedReason == null && word.Status == "approved"
                           && card.DueMs <= endMs
                           && (card.BuriedUntilLocalDate == null || string.Compare(card.BuriedUntilLocalDate, today) < 0)
                       select new { Card = card, Sense = sense, Word = word };
        IQueryable<Card> due = eligible.Where(item => item.Card.State != CardState.New &&
            (item.Card.Direction == "es_en" || database.Cards.Any(sibling => sibling.UserId == item.Card.UserId
                && sibling.SenseId == item.Card.SenseId && sibling.Direction == "es_en" && sibling.State == CardState.Review
                && database.ReviewLogs.Any(log => log.CardId == sibling.Id && log.UserId == sibling.UserId
                    && log.CardEpoch == sibling.Epoch && log.CountsForSrs == true && log.Rating > Rating.Again && log.ReviewedAtMs < startMs))))
            .OrderByDescending(item => item.Card.State == CardState.Learning || item.Card.State == CardState.Relearning)
            .ThenBy(item => item.Card.DueMs).Select(item => item.Card).Take(200);
        IQueryable<Card> fresh = eligible.Where(item => item.Card.State == CardState.New && item.Card.Direction == "es_en")
            .OrderBy(item => item.Word.Cefr).ThenBy(item => item.Word.Id).ThenBy(item => item.Sense.SenseOrder)
            .Select(item => item.Card).Take(12);
        string dueSql = due.ToQueryString();
        string newSql = fresh.ToQueryString();
        foreach (string sql in new[] { dueSql, newSql })
        {
            Assert.Contains("INNER JOIN \"senses\"", sql);
            Assert.Contains("INNER JOIN \"words\"", sql);
            Assert.Contains("WHERE", sql);
            Assert.Contains("\"user_id\" = 1", sql);
            Assert.Contains("\"suspended_reason\" IS NULL", sql);
            Assert.Contains("\"status\" = 'approved'", sql);
            Assert.Contains("\"due_ms\" <=", sql);
            Assert.Contains("\"buried_until_local_date\" <", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.Contains("LIMIT", sql);
        }
        Assert.Contains("EXISTS", dueSql);
        Assert.Contains("\"reviewed_at_ms\" <", dueSql);
        Assert.Contains("\"cefr\"", newSql);
        Assert.Equal(new[] { 3, 2, 9, 1 }, (await due.ToListAsync(CancellationToken.None)).Select(card => card.Id).ToArray());
        Assert.Equal(new[] { 14, 13 }, (await fresh.ToListAsync(CancellationToken.None)).Select(card => card.Id).ToArray());
        Assert.Equal(new[] { 3, 2 }, (await due.Take(2).ToListAsync(CancellationToken.None)).Select(card => card.Id).ToArray());
        Assert.Equal(new[] { 14 }, (await fresh.Take(1).ToListAsync(CancellationToken.None)).Select(card => card.Id).ToArray());
        // Negative control: proves this provider rejects client-only local-date evaluation in filters.
        Assert.Throws<InvalidOperationException>(() => database.Cards
            .Where(card => clock.LocalDateOf(DateTimeOffset.FromUnixTimeMilliseconds(card.DueMs)) == clock.LocalToday)
            .ToQueryString());
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
