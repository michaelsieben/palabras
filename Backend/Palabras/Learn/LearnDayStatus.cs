using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// M7 day tracking: the day_status row, the rolling 7-day skip ledger and the streak.
// Contract sources: design-amendment.md ΔD:280-306 (skip semantics) and ΔF:631-640 (schema);
// design.md:171-179 (what "done" means, the pre-amendment streak wording). All dates are local
// dates in the learner's zone, so every caller passes a LearnerClock built from the injected
// TimeProvider (07-learn-api.md:17).
internal static class LearnDayStatus
{
    internal const string DateFormat = "yyyy-MM-dd";
    // ΔD:282: 1 skip per rolling 7-day window, i.e. [today - 6, today].
    internal const int SkipWindowDays = 7;

    internal sealed record SkipAvailability(bool Available, string? NextAvailable);

    internal static string Key(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseLocalDate(string? raw, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(raw) && DateOnly.TryParseExact(raw.Trim(), DateFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // Only the caller's rows, so no query or body field can read another learner's history
    // (07-learn-api.md:13). The table is one row per local day, so the filter stays small.
    internal static async Task<IReadOnlyDictionary<string, string>> LoadStatusesAsync(
        PalabrasDbContext database, int userId, CancellationToken cancellationToken)
    {
        var rows = await database.DayStatuses.AsNoTracking()
            .Where(row => row.UserId == userId)
            .Select(row => new { row.LocalDate, row.Status })
            .ToListAsync(cancellationToken);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            statuses[row.LocalDate] = row.Status;
        }
        return statuses;
    }

    internal static async Task<IReadOnlyList<string>> LoadActiveSkipDatesAsync(
        PalabrasDbContext database, int userId, CancellationToken cancellationToken) =>
        await database.DaySkips.AsNoTracking()
            .Where(skip => skip.UserId == userId && skip.RefundedAtMs == null)
            .Select(skip => skip.LocalDate)
            .ToListAsync(cancellationToken);

    // ΔD:282-283: allowed iff no non-refunded skip row falls in [today - 6, today]; the next
    // allowed date is the latest consumed skip + 7. A refunded row is not consumed, so the token
    // is back (ΔD:293) and too-old rows have left the window.
    internal static SkipAvailability EvaluateSkip(IEnumerable<string> activeSkipDates, DateOnly today)
    {
        var earliest = today.AddDays(-(SkipWindowDays - 1));
        DateOnly? latest = null;
        foreach (var raw in activeSkipDates)
        {
            if (!TryParseLocalDate(raw, out var date) || date < earliest || date > today)
            {
                continue;
            }
            if (latest is null || date > latest)
            {
                latest = date;
            }
        }
        return latest is { } last
            ? new SkipAvailability(false, Key(last.AddDays(SkipWindowDays)))
            : new SkipAvailability(true, null);
    }

    // ΔD:290-292: the streak is the chain of consecutive local dates with done or skipped,
    // walked backwards from today. A skipped day preserves the chain but does not increment it;
    // a missed or absent day ends it (the ISO-week clause of design.md:179 is removed).
    // When today has no row the chain is anchored at yesterday, because the 22:00 reminder
    // already shows the streak before that day's session exists (ΔC.3, design-amendment.md:232).
    internal static int Streak(IReadOnlyDictionary<string, string> statuses, DateOnly today)
    {
        var anchor = today;
        if (!statuses.TryGetValue(Key(today), out var todayStatus))
        {
            anchor = today.AddDays(-1);
            if (!statuses.TryGetValue(Key(anchor), out var yesterday)
                || !DayStatusValues.KeepsChain(yesterday))
            {
                return 0;
            }
        }
        else if (!DayStatusValues.KeepsChain(todayStatus))
        {
            return 0;
        }
        var streak = 0;
        for (var date = anchor; ; date = date.AddDays(-1))
        {
            if (!statuses.TryGetValue(Key(date), out var status))
            {
                break;
            }
            if (status == DayStatusValues.Done)
            {
                streak++;
            }
            else if (status != DayStatusValues.Skipped)
            {
                break;
            }
        }
        return streak;
    }

    internal static async Task<DayStatusResponse> ReadAsync(PalabrasDbContext database, int userId,
        LearnerClock clock, CancellationToken cancellationToken)
    {
        var today = clock.LocalToday;
        var statuses = await LoadStatusesAsync(database, userId, cancellationToken);
        var activeSkips = await LoadActiveSkipDatesAsync(database, userId, cancellationToken);
        var availability = EvaluateSkip(activeSkips, today);
        statuses.TryGetValue(Key(today), out var status);
        return new DayStatusResponse(Key(today), status, Streak(statuses, today), availability.Available,
            availability.NextAvailable);
    }

    // Payload for a request that already loaded the rows, so a success path needs no second read.
    internal static DayStatusResponse Compose(IReadOnlyDictionary<string, string> statuses,
        IEnumerable<string> activeSkipDates, DateOnly today, string? status)
    {
        var merged = status is null
            ? statuses
            : new Dictionary<string, string>(statuses, StringComparer.Ordinal) { [Key(today)] = status };
        var availability = EvaluateSkip(activeSkipDates, today);
        return new DayStatusResponse(Key(today), status, Streak(merged, today), availability.Available,
            availability.NextAvailable);
    }
}
