using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// M6.6 `GET /api/learn/stats` (design §3). Read-only, learner-only, scoped by the cookie's user
// id; no route or query parameter selects a learner (07-learn-api.md:13). GET, so the
// X-Palabras header rule does not apply (Program.cs:133-142).
//
// Mapped from Program.cs with one line beside MapLearnEndpoints because LearnEndpoints.cs is
// owned by the variety lane in this milestone.
internal static class StatsEndpoints
{
    private const string Policy = "learner";
    private const string FallbackTimezone = "Europe/Madrid";
    private const string DateFormat = "yyyy-MM-dd";
    // design §3 defaults: 30 local days ending today.
    private const int DefaultRangeDays = 30;
    // design §3 errors: a range over 366 days is rejected.
    private const int MaximumRangeDays = 366;

    internal static void MapStatsEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/learn/stats", StatsAsync).RequireAuthorization(Policy);
    }

    private static async Task<IResult> StatsAsync(HttpContext context, DatabaseSettings settings,
        TimeProvider timeProvider, ILoggerFactory loggerFactory, string? from, string? to,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        await using var database = settings.CreateContext();
        var learner = await database.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.Id, user.Timezone }).SingleOrDefaultAsync(cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = CreateClock(timeProvider, learner.Timezone, loggerFactory);
        // A blank parameter is treated as absent so `?from=&to=` behaves like the bare route;
        // anything present but malformed is a 400, never a silent default.
        if (!TryResolveRange(from, to, clock.LocalToday, out var fromDate, out var toDate))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        var input = await LearnStats.LoadAsync(database, userId, clock, fromDate, toDate, cancellationToken);
        return Results.Ok(LearnStats.Aggregate(input, clock, fromDate, toDate));
    }

    private static bool TryResolveRange(string? from, string? to, DateOnly localToday, out DateOnly fromDate,
        out DateOnly toDate)
    {
        toDate = localToday;
        if (!string.IsNullOrWhiteSpace(to) && !TryParseDate(to, out toDate))
        {
            fromDate = default;
            return false;
        }
        fromDate = toDate.AddDays(-(DefaultRangeDays - 1));
        if (!string.IsNullOrWhiteSpace(from) && !TryParseDate(from, out fromDate))
        {
            return false;
        }
        if (fromDate > toDate || toDate > localToday)
        {
            return false;
        }
        return toDate.DayNumber - fromDate.DayNumber + 1 <= MaximumRangeDays;
    }

    private static bool TryParseDate(string raw, out DateOnly date) => DateOnly.TryParseExact(raw.Trim(),
        DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // Same fallback as LearnEndpoints.CreateClock (LearnEndpoints.cs:280-291): an unknown stored
    // zone logs once and uses the product default instead of failing every stats read. Duplicated
    // because LearnEndpoints.cs is another lane's file in this milestone; the two must stay equal.
    private static LearnerClock CreateClock(TimeProvider timeProvider, string? timezone, ILoggerFactory loggerFactory)
    {
        if (!string.IsNullOrWhiteSpace(timezone) && !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
        {
            loggerFactory.CreateLogger("Palabras.Learn").LogWarning(
                "Unknown learner timezone {Timezone}; falling back to {FallbackTimezone}", timezone, FallbackTimezone);
            timezone = null;
        }
        return new LearnerClock(timeProvider, timezone);
    }

    private static int? CurrentUserId(HttpContext context)
    {
        var value = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);
}
