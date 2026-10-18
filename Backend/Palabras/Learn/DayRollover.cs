using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// ΔD:297: at 00:05 local, write `missed` for yesterday when it has no done or skipped row.
// History only: no card, no review_log row and no skip is touched (ΔD:298-300). Idempotent, so
// the minute tick can retry after a failure and a restart cannot duplicate a day.
internal static class DayRollover
{
    internal const string FallbackTimezone = "Europe/Madrid";
    // 00:05 local leaves room for an offline session flush that lands just after midnight.
    internal static readonly TimeSpan EarliestRun = new(0, 5, 0);

    internal static async Task<int> RunAsync(DatabaseSettings settings, TimeProvider timeProvider,
        ILogger logger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var database = settings.CreateContext();
        // The schema permits at most one owner plus one reviewer; both are checked so a future
        // second learner needs no change here.
        var users = await database.Users.AsNoTracking()
            .Select(user => new { user.Id, user.Timezone, user.CreatedAt })
            .ToListAsync(cancellationToken);
        var written = 0;
        foreach (var user in users)
        {
            var zone = ResolveZone(user.Timezone, logger);
            if (TimeZoneInfo.ConvertTime(now, zone).TimeOfDay < EarliestRun)
            {
                continue;
            }
            var clock = new LearnerClock(timeProvider, zone.Id);
            var yesterday = clock.LocalToday.AddDays(-1);
            // A day before the account existed is not a missed day.
            if (clock.LocalDateOf(user.CreatedAt) > yesterday)
            {
                continue;
            }
            var key = LearnDayStatus.Key(yesterday);
            var exists = await database.DayStatuses.AsNoTracking()
                .AnyAsync(row => row.UserId == user.Id && row.LocalDate == key, cancellationToken);
            if (exists)
            {
                continue;
            }
            database.DayStatuses.Add(new DayStatus
            {
                UserId = user.Id,
                LocalDate = key,
                Status = DayStatusValues.Missed,
                SessionId = null,
                DecidedAtMs = now.ToUnixTimeMilliseconds()
            });
            written++;
            logger.LogInformation("Day rollover recorded a missed day for user {UserId} on {LocalDate}",
                user.Id, key);
        }
        if (written > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        return written;
    }

    // Same fallback as LearnEndpoints.CreateClock: an unknown stored zone must not break the
    // rollover for the product's default zone.
    private static TimeZoneInfo ResolveZone(string? timezone, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone))
            {
                return zone;
            }
            logger.LogWarning("Unknown learner timezone {Timezone}; falling back to {FallbackTimezone}",
                timezone, FallbackTimezone);
        }
        return TimeZoneInfo.FindSystemTimeZoneById(FallbackTimezone);
    }
}
