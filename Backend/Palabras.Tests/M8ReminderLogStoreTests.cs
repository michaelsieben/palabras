using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 U5 reminder decision log (plan §2.3/§2.4, design-amendment.md ΔF :642-647). The claim is the
// schema's UNIQUE (user_id, local_date, kind, channel), so the assertions are about the row and the
// constraint, not about an in-memory "already sent?" flag: a duplicate must be impossible even when
// the caller never read first.
public sealed class M8ReminderLogStoreTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly long NowMs =
        DateTimeOffset.Parse("2026-10-20T22:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private readonly string _root = Path.Combine("/tmp", "palabras-push", "log-" + Guid.NewGuid().ToString("N"));

    public M8ReminderLogStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ClaimWritesThePlannedDailyWebPushDecision()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var claim = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);

        Assert.True(claim.Claimed);
        Assert.Equal(1, claim.Row.UserId);
        Assert.Equal("2026-10-20", claim.Row.LocalDate);
        Assert.Equal("daily", claim.Row.Kind);
        Assert.Equal("webpush", claim.Row.Channel);
        Assert.Equal("sending", claim.Row.Result);
        Assert.Equal(300, claim.Row.TtlS);
        Assert.Equal(NowMs + 300_000, claim.Row.ExpiresAtMs);
        Assert.True(claim.Row.SkipAvailable);
        Assert.Equal(NowMs, claim.Row.CreatedAtMs);
        Assert.Equal(NowMs, claim.Row.UpdatedAtMs);
        Assert.Single(await database.ReminderLogs.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASecondClaimOnTheSameDayReturnsTheExistingDecision()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var first = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);
        // The restart case: the row already says today is owned, so a second tick resumes it instead
        // of writing a second decision.
        var second = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: false, ttlSeconds: 120,
            expiresAtMs: NowMs + 120_000, nowMs: NowMs + 60_000, CancellationToken.None);

        Assert.False(second.Claimed);
        Assert.Equal(first.Row.Id, second.Row.Id);
        Assert.Equal(NowMs, second.Row.CreatedAtMs);
        Assert.Equal(300, second.Row.TtlS);
        var rows = await database.ReminderLogs.AsNoTracking().ToListAsync(CancellationToken.None);
        Assert.Single(rows);
    }

    [Fact]
    public async Task TheDailyUniquenessIsEnforcedByTheDatabase()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);
        // Written directly, bypassing the store's own read-ahead: the constraint itself must refuse
        // the second row, because that is what makes the claim safe across processes and restarts.
        database.ReminderLogs.Add(new ReminderLog
        {
            UserId = 1, LocalDate = "2026-10-20", Kind = "daily", Channel = "webpush", Result = "sent",
            SkipAvailable = true, CreatedAtMs = NowMs, UpdatedAtMs = NowMs
        });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync(CancellationToken.None));
        Assert.Contains("UNIQUE", failure.InnerException?.Message ?? failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AClaimForAnotherLocalDayIsItsOwnDecision()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);
        var tomorrow = await ReminderLogStore.ClaimAsync(database, 1, Today.AddDays(1), skipAvailable: true,
            ttlSeconds: 300, expiresAtMs: NowMs + 86_400_000, nowMs: NowMs, CancellationToken.None);

        Assert.True(tomorrow.Claimed);
        var rows = await database.ReminderLogs.AsNoTracking().OrderBy(row => row.LocalDate)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(["2026-10-20", "2026-10-21"], rows.Select(row => row.LocalDate).ToArray());
    }

    [Fact]
    public async Task SuppressRecordsTheReasonOnceAndKeepsTheWinner()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var missed = await ReminderLogStore.SuppressAsync(database, 1, Today, ReminderLogStore.WindowMissedResult,
            skipAvailable: true, nowMs: NowMs, CancellationToken.None);
        Assert.True(missed.Claimed);
        Assert.Equal("window_missed", missed.Row.Result);
        Assert.Null(missed.Row.TtlS);
        Assert.Null(missed.Row.ExpiresAtMs);

        // A later tick (or the next restart) must not overwrite an already recorded why-not.
        var repeated = await ReminderLogStore.SuppressAsync(database, 1, Today, ReminderLogStore.NoTargetResult,
            skipAvailable: false, nowMs: NowMs + 60_000, CancellationToken.None);
        Assert.False(repeated.Claimed);
        Assert.Equal(missed.Row.Id, repeated.Row.Id);
        var row = await database.ReminderLogs.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("window_missed", row.Result);
        Assert.Equal(NowMs, row.UpdatedAtMs);
    }

    [Fact]
    public async Task SuppressDoesNotReplaceAClaimedSend()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var claim = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);
        var suppressed = await ReminderLogStore.SuppressAsync(database, 1, Today,
            ReminderLogStore.WindowMissedResult, skipAvailable: true, nowMs: NowMs + 60_000,
            CancellationToken.None);

        Assert.False(suppressed.Claimed);
        Assert.Equal(claim.Row.Id, suppressed.Row.Id);
        var row = await database.ReminderLogs.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("sending", row.Result);
    }

    [Fact]
    public async Task CompleteFinalisesTheClaimWithTheLastAttemptTtl()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var claim = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);
        var updated = await ReminderLogStore.CompleteAsync(database, claim.Row.Id, ReminderLogStore.SentResult,
            ttlSeconds: 120, expiresAtMs: NowMs + 120_000, nowMs: NowMs + 30_000, CancellationToken.None);

        Assert.True(updated);
        var row = await database.ReminderLogs.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("sent", row.Result);
        Assert.Equal(120, row.TtlS);
        Assert.Equal(NowMs + 120_000, row.ExpiresAtMs);
        Assert.Equal(NowMs, row.CreatedAtMs);
        Assert.Equal(NowMs + 30_000, row.UpdatedAtMs);
    }

    [Fact]
    public async Task CompletingAVanishedRowReportsThatNothingWasRecorded()
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var updated = await ReminderLogStore.CompleteAsync(database, 404, ReminderLogStore.FailedResult,
            ttlSeconds: null, expiresAtMs: null, nowMs: NowMs, CancellationToken.None);

        Assert.False(updated);
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("window_missed")]
    [InlineData("no_target")]
    [InlineData("nonsense")]
    public async Task OnlyFinalResultsCanCompleteADecision(string result)
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        var claim = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReminderLogStore.CompleteAsync(database,
            claim.Row.Id, result, ttlSeconds: null, expiresAtMs: null, nowMs: NowMs, CancellationToken.None));
        Assert.Equal("sending", (await database.ReminderLogs.AsNoTracking().SingleAsync(CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("sent")]
    [InlineData("failed")]
    [InlineData("gone")]
    [InlineData("nonsense")]
    public async Task OnlySuppressedResultsCanSuppressADecision(string result)
    {
        var settings = Seed();

        await using var database = settings.CreateContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReminderLogStore.SuppressAsync(database, 1,
            Today, result, skipAvailable: true, nowMs: NowMs, CancellationToken.None));
        Assert.Empty(await database.ReminderLogs.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindAnswersOnlyForTheSameLearnerAndDay()
    {
        var settings = Seed(secondUser: true);

        await using var database = settings.CreateContext();
        var claim = await ReminderLogStore.ClaimAsync(database, 1, Today, skipAvailable: true, ttlSeconds: 300,
            expiresAtMs: NowMs + 300_000, nowMs: NowMs, CancellationToken.None);

        var found = await ReminderLogStore.FindAsync(database, 1, Today, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(claim.Row.Id, found!.Id);
        Assert.Equal("sending", found.Result);
        Assert.Null(await ReminderLogStore.FindAsync(database, 1, Today.AddDays(-1), CancellationToken.None));
        Assert.Null(await ReminderLogStore.FindAsync(database, 2, Today, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DatabaseSettings Seed(bool secondUser = false)
    {
        var settings = new DatabaseSettings(Path.Combine(_root, "palabras.db"));
        using var database = settings.CreateContext();
        database.Database.Migrate();
        var stamp = DateTimeOffset.UnixEpoch;
        database.Users.Add(new User
        {
            Id = 1, Username = "learner", Role = "owner", PasswordHash = "hash", CreatedAt = stamp, UpdatedAt = stamp
        });
        if (secondUser)
        {
            database.Users.Add(new User
            {
                Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = "hash", CreatedAt = stamp,
                UpdatedAt = stamp
            });
        }
        database.SaveChanges();
        return settings;
    }
}
