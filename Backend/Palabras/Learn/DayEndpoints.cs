using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// M7 day tracking routes: POST /api/learn/day/skip (design-amendment.md ΔD:280-289),
// GET /api/learn/day/status and POST /api/learn/day/done (see DayContracts.cs; both are M7
// additions whose route is not fixed by the docs). Learner-only, scoped by the cookie's user
// id like every other /api/learn route (07-learn-api.md:9-13). The X-Palabras rule for
// non-GET calls is enforced by the middleware in Program.cs:133-142.
internal static class DayEndpoints
{
    private const string Policy = "learner";

    internal static void MapDayEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/learn").RequireAuthorization(Policy);
        group.MapGet("/day/status", StatusAsync);
        group.MapPost("/day/skip", SkipAsync);
        group.MapPost("/day/done", DoneAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext context, DatabaseSettings settings,
        TimeProvider timeProvider, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        await using var database = settings.CreateContext();
        var learner = await LearnEndpoints.LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = LearnEndpoints.CreateClock(timeProvider, learner.Timezone, loggerFactory);
        return Results.Ok(await LearnDayStatus.ReadAsync(database, userId, clock, cancellationToken));
    }

    private static async Task<IResult> SkipAsync(SkipRequest? request, HttpContext context,
        DatabaseSettings settings, TimeProvider timeProvider, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        if (request is null || !SkipSources.IsKnown(request.Source)
            || !LearnDayStatus.TryParseLocalDate(request.LocalDate, out var requestedDate))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        await using var database = settings.CreateContext();
        var learner = await LearnEndpoints.LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = LearnEndpoints.CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var today = clock.LocalToday;
        // ΔD:281: a skip is only for today in the learner's zone, so a stale notification click
        // after local midnight cannot spend tomorrow's token.
        if (requestedDate != today)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        var key = LearnDayStatus.Key(today);

        // Read-only answer first: already done and already skipped need no write.
        var statuses = await LearnDayStatus.LoadStatusesAsync(database, userId, cancellationToken);
        var activeSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId, cancellationToken);
        if (StatusOn(statuses, key) == DayStatusValues.Done)
        {
            return AlreadyDone();
        }
        if (StatusOn(statuses, key) == DayStatusValues.Skipped)
        {
            return Results.Ok(LearnDayStatus.Compose(statuses, activeSkips, today, DayStatusValues.Skipped));
        }
        var availability = LearnDayStatus.EvaluateSkip(activeSkips, today);
        if (!availability.Available)
        {
            return SkipUnavailable(availability.NextAvailable);
        }

        // ΔD:284: the check and the insert are one transaction and SQLite's single writer
        // serialises them. The transaction is immediate, so a second click waits for the first
        // commit and then re-reads the ledger instead of both passing the check.
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using (var transaction = await WriteTransaction.BeginAsync(database, cancellationToken))
        {
            var day = await database.DayStatuses.SingleOrDefaultAsync(
                row => row.UserId == userId && row.LocalDate == key, cancellationToken);
            if (day?.Status == DayStatusValues.Done)
            {
                return AlreadyDone();
            }
            if (day?.Status == DayStatusValues.Skipped)
            {
                // Re-read the ledger: another click may have consumed the token between the
                // optimistic read above and this transaction.
                var currentSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId,
                    cancellationToken);
                return Results.Ok(LearnDayStatus.Compose(statuses, currentSkips, today, DayStatusValues.Skipped));
            }
            var windowSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId, cancellationToken);
            var windowAvailability = LearnDayStatus.EvaluateSkip(windowSkips, today);
            if (!windowAvailability.Available)
            {
                return SkipUnavailable(windowAvailability.NextAvailable);
            }
            database.DaySkips.Add(new DaySkip
            {
                UserId = userId,
                LocalDate = key,
                Source = request.Source!,
                CreatedAtMs = nowMs
            });
            if (day is null)
            {
                day = new DayStatus { UserId = userId, LocalDate = key };
                database.DayStatuses.Add(day);
            }
            day.Status = DayStatusValues.Skipped;
            day.SessionId = null;
            day.DecidedAtMs = nowMs;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // Read after the transaction scope is gone: SQLite rejects a command tied to a completed
        // transaction, and the caller's payload must reflect the committed row.
        return Results.Ok(await LearnDayStatus.ReadAsync(database, userId, clock, cancellationToken));
    }

    private static async Task<IResult> DoneAsync(DayDoneRequest? request, HttpContext context,
        DatabaseSettings settings, TimeProvider timeProvider, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        if (request is null || request.DurationS is < 0 || request.PlannedS is < 0
            || request.NewCount is < 0 || request.ReviewCount is < 0)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        await using var database = settings.CreateContext();
        var learner = await LearnEndpoints.LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = LearnEndpoints.CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var today = clock.LocalToday;
        // O-M7-9 (C5): the offline flush may name the local day the session actually completed
        // on. Absent means the receipt day (pre-M7-9 behavior, old callers unchanged). A named
        // day must parse exactly and sit inside the agreed bounded window — today or yesterday
        // in the learner zone, never before the account existed — or the request is refused;
        // a retry that arrives after the window rejects, it never silently completes a new day.
        var target = today;
        if (request.LocalDate is not null)
        {
            if (!LearnDayStatus.TryParseLocalDate(request.LocalDate, out var requested)
                || requested > today
                || requested < today.AddDays(-1)
                || requested < clock.LocalDateOf(learner.CreatedAt))
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }
            target = requested;
        }
        var key = LearnDayStatus.Key(target);

        // Repeat completion (offline flush retry, double tap) is a no-op on the target day: it
        // stays done and no second session row is written.
        var statuses = await LearnDayStatus.LoadStatusesAsync(database, userId, cancellationToken);
        var activeSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId, cancellationToken);
        if (StatusOn(statuses, key) == DayStatusValues.Done)
        {
            return Results.Ok(LearnDayStatus.Compose(statuses, activeSkips, target, DayStatusValues.Done));
        }

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using (var transaction = await WriteTransaction.BeginAsync(database, cancellationToken))
        {
            var day = await database.DayStatuses.SingleOrDefaultAsync(
                row => row.UserId == userId && row.LocalDate == key, cancellationToken);
            if (day?.Status == DayStatusValues.Done)
            {
                // Another request completed the day first; report the committed state.
                var currentSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId,
                    cancellationToken);
                return Results.Ok(LearnDayStatus.Compose(statuses, currentSkips, target, DayStatusValues.Done));
            }
            // design.md:171: a completed session makes the day done. The session row keeps the
            // reported duration/counts; started_at is unknown to this endpoint and is stamped now.
            var session = new PracticeSession
            {
                UserId = userId,
                LocalDate = key,
                StartedAtMs = nowMs,
                CompletedAtMs = nowMs,
                DurationS = request.DurationS,
                PlannedS = request.PlannedS,
                NewCount = request.NewCount ?? 0,
                ReviewCount = request.ReviewCount ?? 0,
                Status = "completed"
            };
            database.PracticeSessions.Add(session);
            await database.SaveChangesAsync(cancellationToken);
            if (day is null)
            {
                day = new DayStatus { UserId = userId, LocalDate = key };
                database.DayStatuses.Add(day);
            }
            day.Status = DayStatusValues.Done;
            day.SessionId = session.Id;
            day.DecidedAtMs = nowMs;
            // ΔD:293: a skip on the completed day is refunded once that day is completed, so
            // the token is available again. A skip on another day stays consumed. With a named
            // target the refund follows the target date, not the receipt date (C5).
            var refund = await database.DaySkips.SingleOrDefaultAsync(
                skip => skip.UserId == userId && skip.LocalDate == key && skip.RefundedAtMs == null,
                cancellationToken);
            if (refund is not null)
            {
                refund.RefundedAtMs = nowMs;
            }
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // The payload describes the target day, not the receipt day (C5 decision): a completion
        // for yesterday reported after midnight shows yesterday's streak; the caller's today
        // pane refetches GET /day/status for the receipt day.
        var committedStatuses = await LearnDayStatus.LoadStatusesAsync(database, userId, cancellationToken);
        var committedSkips = await LearnDayStatus.LoadActiveSkipDatesAsync(database, userId, cancellationToken);
        return Results.Ok(LearnDayStatus.Compose(committedStatuses, committedSkips, target, DayStatusValues.Done));
    }

    private static string? StatusOn(IReadOnlyDictionary<string, string> statuses, string key) =>
        statuses.TryGetValue(key, out var status) ? status : null;

    private static IResult AlreadyDone() =>
        Results.Json(new { reason = "already_done" }, statusCode: StatusCodes.Status409Conflict);

    // O-M7-5 (decided T-20261018-2105): camelCase `nextAvailable` follows the global API
    // serialization convention (07-learn-api.md:11-17) over the ΔD:288 snake_case sketch.
    private static IResult SkipUnavailable(string? nextAvailable) =>
        Results.Json(new { reason = "skip_unavailable", nextAvailable },
            statusCode: StatusCodes.Status409Conflict);

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);

    // SQLite's provider starts deferred transactions, which would let two clicks both pass the
    // window check before either writes. `deferred: false` takes the write lock first.
    private sealed class WriteTransaction : IAsyncDisposable
    {
        private readonly DbTransaction _transaction;
        private readonly IDbContextTransaction _scope;

        private WriteTransaction(DbTransaction transaction, IDbContextTransaction scope)
        {
            _transaction = transaction;
            _scope = scope;
        }

        internal static async Task<WriteTransaction> BeginAsync(PalabrasDbContext database,
            CancellationToken cancellationToken)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)database.Database.GetDbConnection();
            var transaction = connection.BeginTransaction(deferred: false);
            var scope = database.Database.UseTransaction(transaction)
                ?? throw new InvalidOperationException("EF Core did not accept the SQLite write transaction.");
            return new WriteTransaction(transaction, scope);
        }

        internal Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _transaction.DisposeAsync();
        }
    }
}
