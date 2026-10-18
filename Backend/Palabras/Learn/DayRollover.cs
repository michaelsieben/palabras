using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// ΔD:297: at 00:05 local, write `missed` for yesterday when it has no done or skipped row.
// History only: no card, no review_log row and no skip is touched (ΔD:298-300). Idempotent, so
// the minute tick can retry after a failure and a restart cannot duplicate a day. Runs only
// behind the DayRolloverSettings gate and only for days that began at or after its durable
// cutover (see EnsureCutoverAsync and the C1 guards in RunAsync and DayRolloverService), and
// only for the owner/learner account (C4 — see LearnerRole below).
internal static class DayRollover
{
    internal const string FallbackTimezone = "Europe/Madrid";

    // app_meta key holding the rollover cutover as a UTC unix-ms decimal string (the repo-wide
    // instant convention, like day_status.decided_at_ms).
    internal const string CutoverKey = "m7_rollover_cutover_ms";

    // 00:05 local leaves room for an offline session flush that lands just after midnight.
    internal static readonly TimeSpan EarliestRun = new(0, 5, 0);

    // Durable cutover (T-20261018-2105 step 11). The first enabled observation of this database
    // fixes the cutover to the instant it sees and persists it in app_meta; every later tick,
    // host restart, redeploy or re-enable reads that same instant back, so a restart can no
    // longer move the cutover forward and strand the day under way without a history row.
    // Consequences kept from C1: a day that began before the cutover is refused forever and no
    // history is ever invented for the untracked period. When the marker is absent — a fresh
    // database, or one created before this change — the cutover is THIS observation (the safe
    // choice: the enable day stays a partial, never-marked day and nothing is backfilled).
    // A concurrent second writer loses the PK conflict and adopts the stored value, so the
    // recorded instant is always the first successfully persisted one. A marker that is corrupt,
    // truncated or outside the DateTimeOffset range re-anchors to now through a conditional
    // write, so two instances cannot keep two different cutovers.
    internal static async Task<DateTimeOffset> EnsureCutoverAsync(DatabaseSettings settings, TimeProvider time,
        ILogger logger, CancellationToken cancellationToken)
    {
        await using var database = settings.CreateContext();
        // AsNoTracking: the absent-marker branch relies on the primary key to pick a single winner,
        // and the corrupt branch writes through a conditional ExecuteUpdate, so a tracked copy
        // could only be saved over another instance's value.
        var marker = await database.AppMetas.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == CutoverKey, cancellationToken);
        if (marker is null)
        {
            var now = time.GetUtcNow();
            database.AppMetas.Add(new AppMeta { Key = CutoverKey, Value = Format(now) });
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
            {
                // Another observation won the insert; its instant is the durable answer. AsNoTracking
                // bypasses this context's own failed Added entity.
                var stored = await database.AppMetas.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.Key == CutoverKey, cancellationToken);
                if (stored is not null && TryRead(stored.Value, out var adopted))
                {
                    return adopted;
                }
                throw;
            }
            // Logged only after the insert committed (review C6): a failed save must not claim a
            // marker that was never written.
            logger.LogInformation("Day rollover cutover recorded for this database at {Cutover}: local " +
                "days that began before it are never marked missed, and restarts reuse it unchanged.", now);
            return now;
        }
        if (TryRead(marker.Value, out var persisted))
        {
            return persisted;
        }
        // A hand-edited, truncated or out-of-range marker must not silently decide the cutover;
        // re-anchor to now so the refusal window can only shrink the tracked span, never invent
        // rows. The write is conditional on the exact value observed (review F3): two instances
        // that both saw the corrupt marker cannot each keep their own "now" — whoever loses the
        // conditional update adopts the winner's stored instant below instead.
        var observed = marker.Value;
        var reanchored = time.GetUtcNow();
        var updated = await database.AppMetas
            .Where(row => row.Key == CutoverKey && row.Value == observed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Value, Format(reanchored)),
                cancellationToken);
        if (updated > 0)
        {
            logger.LogWarning("Day rollover cutover marker {Key} is unreadable ({Value}); re-anchored to {Cutover}.",
                CutoverKey, observed, reanchored);
            return reanchored;
        }
        // The marker changed between our read and our conditional write. Adopt what is persisted
        // now instead of keeping a competing in-memory instant; a value that is still unreadable
        // means another instance is mid-re-anchor, and the next tick retries.
        var raced = await database.AppMetas.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == CutoverKey, cancellationToken);
        if (raced is not null && TryRead(raced.Value, out var winner))
        {
            logger.LogInformation("Day rollover cutover marker {Key} was re-anchored by a concurrent " +
                "instance to {Cutover}; adopting it.", CutoverKey, winner);
            return winner;
        }
        throw new InvalidOperationException(
            $"Day rollover cutover marker {CutoverKey} kept changing while it was being re-anchored.");
    }

    private static string Format(DateTimeOffset instant) =>
        instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    // DateTimeOffset.FromUnixTimeMilliseconds throws outside 0001-01-01T00:00:00Z ..
    // 9999-12-31T23:59:59.999Z, so a marker past that range is unreadable rather than fatal
    // (review F2): it takes the same re-anchor path as any other corrupt value instead of
    // throwing out of every tick.
    private static readonly long MaxUnixTimeMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private static bool TryRead(string value, out DateTimeOffset instant)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds >= 0 && milliseconds <= MaxUnixTimeMilliseconds)
        {
            instant = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
        instant = default;
        return false;
    }

    // C4 (T-20261018-2105, decided before enablement): only the owner account is the learner.
    // AccountAuthentication.Roles grants the "learner" policy to Role == "owner" only, the CHECK
    // constraint ck_users_role admits exactly owner and reviewer, and the unique role index
    // permits at most one owner — so "owner" names precisely the account that completes days and
    // can therefore be judged missed. The reviewer has no day-completion signal and must never
    // receive `missed` history.
    internal const string LearnerRole = "owner";

    internal static async Task<int> RunAsync(DatabaseSettings settings, TimeProvider timeProvider,
        ILogger logger, DateTimeOffset trackingStart, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var database = settings.CreateContext();
        var users = await database.Users.AsNoTracking()
            .Where(user => user.Role == LearnerRole)
            .Select(user => new { user.Id, user.Timezone, user.CreatedAt })
            .ToListAsync(cancellationToken);
        var written = 0;
        var recorded = new List<(int UserId, string LocalDate)>();
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
            // C1 backfill guard (T-20261018-2105): a day may only be marked missed if the
            // rollover was enabled before that day began — while the gate was off the client
            // had no day-completion signal, so an absent row there is not evidence of a
            // missed day. This caps the first post-enable ticks: the enable day itself and
            // every earlier day stay unwritten, and the first honest candidate is the first
            // full local day after the cutover (C4).
            if (trackingStart > clock.StartOfLocalDateUtc(yesterday))
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
            recorded.Add((user.Id, key));
        }
        if (written > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
            // Logged only after the commit (review C6): a failed save must not leave a success
            // line claiming history rows that were never written.
            foreach (var (userId, localDate) in recorded)
            {
                logger.LogInformation("Day rollover recorded a missed day for user {UserId} on {LocalDate}",
                    userId, localDate);
            }
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
