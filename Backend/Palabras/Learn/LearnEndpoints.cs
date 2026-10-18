using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// Plan §4 API contracts. Every route is learner-only and scoped by the cookie's user id;
// no route or body parameter ever selects the learner.
internal static class LearnEndpoints
{
    private const string Policy = "learner";
    private const string FallbackTimezone = "Europe/Madrid";
    private const int MaximumAttemptsPerBatch = 50;

    internal static void MapLearnEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/learn").RequireAuthorization(Policy);
        group.MapGet("/session/plan", PlanAsync);
        group.MapPost("/attempts", AttemptsAsync);
        group.MapPost("/cards/{id:int}/bury", BuryAsync);
        group.MapPost("/cards/{id:int}/suspend", SuspendAsync);
        group.MapPost("/cards/{id:int}/unsuspend", UnsuspendAsync);
        group.MapGet("/forecast", ForecastAsync);
        group.MapGet("/words", WordsAsync);
    }

    private static async Task<IResult> PlanAsync(HttpContext context, DatabaseSettings settings,
        TimeProvider timeProvider, ILoggerFactory loggerFactory, string? maxNew, string? budgetS,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        if (!TryParseOptionalCount(maxNew, out var newOverride) || !TryParseOptionalCount(budgetS, out var budget))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        await using var database = settings.CreateContext();
        var learner = await LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var learnerSettings = await LoadSettingsAsync(database, userId, cancellationToken);
        var bounds = QueueBounds.From(clock, userId);
        var active = await ActiveSenses.LoadAsync(database, null, cancellationToken);
        var plan = await PlanComposer.ComposeAsync(database, bounds, active, clock, learnerSettings, newOverride,
            budget, cancellationToken);
        return Results.Ok(new PlanResponse(Guid.NewGuid().ToString("D"), timeProvider.GetUtcNow(), bounds.LocalToday,
            plan.Items, new PlanCounts(plan.Due, plan.NewOffered, plan.Backlog), plan.Warnings, plan.NextDueUtc));
    }

    private static async Task<IResult> AttemptsAsync(AttemptsRequest? request, HttpContext context,
        DatabaseSettings settings, TimeProvider timeProvider, IScheduler scheduler, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var attempts = request?.Attempts;
        if (attempts is null || attempts.Count is < 1 or > MaximumAttemptsPerBatch)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        IReadOnlyList<LearnAttempts.NormalizedAttempt> normalized;
        try
        {
            normalized = LearnAttempts.Normalize(attempts);
        }
        catch (ArgumentException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        await using var database = settings.CreateContext();
        var learner = await LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var learnerSettings = await LoadSettingsAsync(database, userId, cancellationToken);
        var results = await LearnAttempts.ApplyAsync(database, userId, clock, learnerSettings, normalized,
            timeProvider.GetUtcNow(), scheduler, cancellationToken);
        return Results.Ok(new AttemptsResponse(results));
    }

    private static Task<IResult> BuryAsync(HttpContext context, DatabaseSettings settings, TimeProvider timeProvider,
        ILoggerFactory loggerFactory, int id, CancellationToken cancellationToken)
        => ChangeCardStateAsync(context, settings, timeProvider, loggerFactory, id, CardAction.Bury, cancellationToken);

    private static Task<IResult> SuspendAsync(HttpContext context, DatabaseSettings settings, TimeProvider timeProvider,
        ILoggerFactory loggerFactory, int id, CancellationToken cancellationToken)
        => ChangeCardStateAsync(context, settings, timeProvider, loggerFactory, id, CardAction.Suspend, cancellationToken);

    private static Task<IResult> UnsuspendAsync(HttpContext context, DatabaseSettings settings, TimeProvider timeProvider,
        ILoggerFactory loggerFactory, int id, CancellationToken cancellationToken)
        => ChangeCardStateAsync(context, settings, timeProvider, loggerFactory, id, CardAction.Unsuspend, cancellationToken);

    private enum CardAction { Bury, Suspend, Unsuspend }

    private static async Task<IResult> ChangeCardStateAsync(HttpContext context, DatabaseSettings settings,
        TimeProvider timeProvider, ILoggerFactory loggerFactory, int id, CardAction action,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        await using var database = settings.CreateContext();
        var card = await database.Cards.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId,
            cancellationToken);
        if (card is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found");
        }
        switch (action)
        {
            case CardAction.Bury:
            {
                var learner = await LoadLearnerAsync(database, userId, cancellationToken);
                if (learner is null)
                {
                    return Error(StatusCodes.Status401Unauthorized, "unauthorized");
                }
                var clock = CreateClock(timeProvider, learner.Timezone, loggerFactory);
                // Plan §4 "buried until local tomorrow": the column holds the LAST local date the
                // card stays out of the queue, and LearnQueries keeps it out while that date is not
                // before today. Storing today therefore hides it for the rest of today only and the
                // card is back in tomorrow's plan (a +1 here hid it for two days).
                card.BuriedUntilLocalDate = clock.LocalToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            }
            case CardAction.Suspend:
                // Plan §4: content-owned suspensions are not the learner's to overwrite.
                if (card.SuspendedReason is "word_not_approved" or "sense_removed")
                {
                    return Error(StatusCodes.Status409Conflict, "not_suspendable");
                }
                card.SuspendedReason = "manual";
                break;
            case CardAction.Unsuspend:
            {
                // Must never resurrect a demoted word or a dropped sense (plan §4, §3.4 S6b rules).
                if (card.SuspendedReason is "word_not_approved" or "sense_removed")
                {
                    return Error(StatusCodes.Status409Conflict, "not_suspendable");
                }
                // A retained removed sense can carry a manual/leech reason instead of
                // sense_removed (S6b kept protected pauses), so the reason alone cannot prove the
                // content is still live. Clearing it would put the card back into plans.
                var wordId = await database.Senses.AsNoTracking()
                    .Where(sense => sense.Id == card.SenseId).Select(sense => (int?)sense.WordId)
                    .SingleOrDefaultAsync(cancellationToken);
                var active = await ActiveSenses.LoadAsync(database,
                    wordId is { } value ? [value] : [], cancellationToken);
                if (!active.IsActive(card.SenseId))
                {
                    return Error(StatusCodes.Status409Conflict, "not_suspendable");
                }
                card.SuspendedReason = null;
                // Clears manual/leech; the lapse count stays so the optimiser keeps the history.
                card.IsLeech = false;
                break;
            }
        }
        await database.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ForecastAsync(HttpContext context, DatabaseSettings settings,
        TimeProvider timeProvider, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        await using var database = settings.CreateContext();
        var learner = await LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var bounds = QueueBounds.From(clock, userId);
        var active = await ActiveSenses.LoadAsync(database, null, cancellationToken);
        var days = new List<ForecastDay>(7);
        for (var offset = 1; offset <= 7; offset++)
        {
            var date = clock.LocalToday.AddDays(offset);
            var due = await LearnQueries.CountDueOnLocalDateAsync(database, bounds, active,
                clock.StartOfLocalDateUtc(date).ToUnixTimeMilliseconds(),
                clock.EndOfLocalDateUtc(date).ToUnixTimeMilliseconds(), cancellationToken);
            days.Add(new ForecastDay(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due));
        }
        return Results.Ok(new ForecastResponse(days[0].Due, days));
    }

    private static async Task<IResult> WordsAsync(HttpContext context, DatabaseSettings settings, string? q, string? topic,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        await using var database = settings.CreateContext();
        IQueryable<Word> words = database.Words.AsNoTracking().Where(word => word.Status == "approved");
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + EscapeLike(q.Trim()) + "%";
            words = words.Where(word => EF.Functions.Like(word.Headword, pattern, "\\")
                || EF.Functions.Like(word.Key, pattern, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(topic))
        {
            var selected = topic.Trim();
            words = words.Where(word => database.WordTopics.Any(link => link.WordId == word.Id && link.Topic == selected));
        }
        var rows = await words.OrderBy(word => word.Cefr).ThenBy(word => word.Headword)
            .Select(word => new { word.Id, word.Key, word.Article, word.Headword })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return Results.Ok(new WordsResponse([]));
        }
        var wordIds = rows.Select(row => row.Id).ToArray();
        // Retained removed senses are still rows; drop them before deriving gloss, topics and stage.
        var active = await ActiveSenses.LoadAsync(database, wordIds, cancellationToken);
        var senses = await database.Senses.AsNoTracking().Where(sense => wordIds.Contains(sense.WordId))
            .OrderBy(sense => sense.SenseOrder)
            .Select(sense => new { sense.Id, sense.WordId, sense.SenseOrder, sense.EnGloss })
            .ToListAsync(cancellationToken);
        senses = senses.Where(sense => active.IsActive(sense.Id)).ToList();
        var topics = await database.WordTopics.AsNoTracking().Where(link => wordIds.Contains(link.WordId))
            .Select(link => new { link.WordId, link.Topic }).ToListAsync(cancellationToken);
        var cards = await database.Cards.AsNoTracking()
            .Where(card => card.UserId == userId
                && database.Senses.Any(sense => sense.Id == card.SenseId && wordIds.Contains(sense.WordId)))
            .ToListAsync(cancellationToken);
        // Sense id -> word id lookup for stage aggregation (both directions per sense).
        var senseWord = senses.ToDictionary(sense => sense.Id, sense => sense.WordId);
        var gloss = senses.GroupBy(sense => sense.WordId)
            .ToDictionary(group => group.Key, group => group.OrderBy(sense => sense.SenseOrder).First().EnGloss);
        var cardsByWord = cards.Where(card => senseWord.ContainsKey(card.SenseId))
            .GroupBy(card => senseWord[card.SenseId]).ToDictionary(group => group.Key, group => group.ToArray());
        var topicsByWord = topics.GroupBy(link => link.WordId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(link => link.Topic)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray());
        var items = rows.Where(row => gloss.ContainsKey(row.Id))
            .Select(row => new WordListItem(row.Key, row.Article, row.Headword,
                gloss[row.Id],
                topicsByWord.GetValueOrDefault(row.Id, []),
                PlanComposer.Stage(cardsByWord.GetValueOrDefault(row.Id, [])))).ToArray();
        return Results.Ok(new WordsResponse(items));
    }

    private sealed record LearnerIdentity(int Id, string? Timezone);

    private static async Task<LearnerIdentity?> LoadLearnerAsync(PalabrasDbContext database, int userId,
        CancellationToken cancellationToken)
    {
        var row = await database.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.Id, user.Timezone }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new LearnerIdentity(row.Id, row.Timezone);
    }

    private static async Task<UserSettings> LoadSettingsAsync(PalabrasDbContext database, int userId,
        CancellationToken cancellationToken)
    {
        var settings = await database.UserSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        return settings ?? new UserSettings { UserId = userId };
    }

    private static LearnerClock CreateClock(TimeProvider timeProvider, string? timezone, ILoggerFactory loggerFactory)
    {
        if (!string.IsNullOrWhiteSpace(timezone) && !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
        {
            // A bad stored zone must not take the whole learner API down; the fallback is the
            // product default (plan §3). Structured so the bad value is greppable.
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

    private static bool TryParseOptionalCount(string? raw, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);
}
