using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Palabras.Learn;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Push;

// M8 U6 reminder trigger (plan §2.4): one minute tick inside the existing container — no cron, no
// second service (DayRolloverService is the proven pattern). It is a separate BackgroundService from
// the rollover on purpose: it reads day_status only to answer "did the learner already close today"
// and never writes it, never reads DayRolloverSettings and never touches the M7 gate, so the gate's
// value cannot change a single reminder decision.
//
// The tick decides with the same clock the M7 work uses (LearnerClock per learner zone), claims the
// day once inside BEGIN IMMEDIATE, then delivers per device with a push_deliveries budget. Delivery is
// at-least-once within the window and the Topic (`daily-<localDate>`) makes a re-send replace an
// undelivered one at the push service, so a crash between "sent" and "recorded" is harmless.
internal sealed class ReminderService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly DatabaseSettings _database;
    private readonly PushSettings _settings;
    private readonly VapidKeyStore _vapid;
    private readonly ReminderClaimWriter _claims;
    private readonly PushSender _sender;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(DatabaseSettings database, PushSettings settings, VapidKeyStore vapid,
        ReminderClaimWriter claims, PushSender sender, TimeProvider time, ILoggerFactory loggerFactory,
        ILogger<ReminderService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(vapid);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _settings = settings;
        _vapid = vapid;
        _claims = claims;
        _sender = sender;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    internal bool Enabled => _settings.Enabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation(
                "Reminders are disabled ({Setting} is not true): no tick runs and nothing is sent.",
                PushSettings.EnabledKey);
            return;
        }
        // The first tick runs immediately (unlike the rollover): a restart at 22:05 must still send
        // today's reminder, and the claim is what makes "once" hold across that restart.
        await RunSafelyAsync(stoppingToken);
        // Driven by the injected clock so a test can advance time instead of waiting a real minute.
        using var timer = new PeriodicTimer(Tick, _time);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            await RunSafelyAsync(stoppingToken);
        }
    }

    // A failed tick must never kill the host: it is retried a minute later, and the day's claim is
    // what keeps that retry from sending twice.
    private async Task RunSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The reminder tick failed; it retries on the next minute tick.");
        }
    }

    // Split out like DayRolloverService.RunTickAsync so tests drive one tick with a fake clock.
    internal async Task<ReminderTick> RunTickAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return ReminderTick.None;
        }
        if (_vapid.Keys is not { } keys)
        {
            // Push is enabled but the key file is unusable: startup already logged the one operator
            // line. A tick without keys cannot sign anything and must not write decisions for a
            // delivery that could never happen.
            return ReminderTick.None;
        }
        var learners = await LoadLearnersAsync(cancellationToken);
        // The tick's single clock read (U6 review M4): every learner's "today", window comparison and
        // `updated_at_ms` in this tick derive from this one instant, so a tick that straddles local
        // midnight cannot plan one day's reminder and claim another.
        var tickAt = _time.GetUtcNow();
        var decisions = 0;
        var delivered = 0;
        foreach (var learner in learners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await RunLearnerAsync(learner, keys, tickAt, cancellationToken);
                if (outcome.Decided)
                {
                    decisions++;
                }
                if (outcome.Delivered)
                {
                    delivered++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One learner's failure (a write lock that outlived the retries, a transport fault)
                // must not stop every other learner's reminder.
                _logger.LogError(exception,
                    "The reminder tick failed for user {UserId}; it retries on the next minute tick.",
                    learner.UserId);
            }
        }
        return new ReminderTick(learners.Count, decisions, delivered);
    }

    // Every active subscription row, with its owner: the account that owns a subscription is the
    // account that learns (the push routes are learner-only, and Roles() grants "learner" to the owner
    // alone), so rows bound to a rotated-out VAPID key are still loaded. That is what lets the tick
    // record `no_target` for a day it cannot deliver instead of leaving the day with no decision.
    private async Task<IReadOnlyList<ReminderLearner>> LoadLearnersAsync(CancellationToken cancellationToken)
    {
        await using var database = _database.CreateContext();
        var rows = await (
                from subscription in database.PushSubscriptions.AsNoTracking()
                join user in database.Users.AsNoTracking() on subscription.UserId equals user.Id
                where subscription.State == PushSubscriptionLifecycle.Active
                    && user.Role == DayRollover.LearnerRole
                orderby subscription.UserId, subscription.Id
                select new { subscription.UserId, subscription.Id, subscription.VapidKeyId })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.UserId)
            .Select(group => new ReminderLearner(group.Key,
                group.Select(row => new ReminderTarget(row.Id, row.VapidKeyId)).ToList()))
            .ToList();
    }

    private async Task<ReminderLearnerOutcome> RunLearnerAsync(ReminderLearner learner, VapidKeys keys,
        DateTimeOffset tickAt, CancellationToken cancellationToken)
    {
        await using var database = _database.CreateContext();
        var identity = await LearnEndpoints.LoadLearnerAsync(database, learner.UserId, cancellationToken);
        if (identity is null)
        {
            return ReminderLearnerOutcome.None;
        }
        var clock = LearnEndpoints.CreateClock(_time, identity.Timezone, _loggerFactory);
        // One instant for the whole learner: the date comes from the same conversion the planner and
        // the payload use, never from a second clock read that could already be on the next day.
        var nowLocal = clock.LocalTimeOf(tickAt);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        await SweepStaleClaimsAsync(database, learner.UserId, today, nowLocal, cancellationToken);
        var status = await LearnDayStatus.ReadAsync(database, learner.UserId, today, cancellationToken);
        var stored = await database.ReminderSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == learner.UserId, cancellationToken);
        var plan = ReminderPlanner.Evaluate(ReminderPreferences.From(stored), status.Status, nowLocal);
        var existing = await ReminderLogStore.FindAsync(database, learner.UserId, today, cancellationToken);
        if (existing is null)
        {
            return await ClaimOrSuppressAsync(learner, plan, status, keys, nowLocal, cancellationToken);
        }
        // Branch on the row's own state, never on "did I claim it": a row that already left `sending`
        // is decided for today (sent, failed, gone, window_missed or no_target) and must not be
        // reopened; a row still `sending` is this tick's resumption of an unfinished send.
        if (existing.Result != ReminderLogStore.SendingResult)
        {
            return ReminderLearnerOutcome.None;
        }
        return await DeliverAsync(learner.UserId, existing, plan, status, keys, nowLocal, cancellationToken);
    }

    // U6 review M1: a claim whose send outlived the learner's local midnight is invisible to a
    // lookup keyed on today, so without this it would stay `sending` forever and the audit would
    // never say what happened. Finalise every unfinished claim from an earlier local day before
    // deciding today's, through the same single transition out of `sending`.
    //
    // Cost: the WHERE is an equality seek on user_id, the leading column of the UNIQUE
    // (user_id, local_date, kind, channel) index, so it reads at most the rows of this one learner
    // (one per day ever reminded) and never scans reminder_log; the "earlier day" and "still sending"
    // filters are applied to that handful of rows in memory. Normally the result is zero rows.
    //
    // If another process is still mid-send for that earlier day, this can finalise ahead of its 201 —
    // the same at-least-once overlap the claim writer documents (M6), now bounded to a claim that was
    // still running after local midnight.
    private async Task SweepStaleClaimsAsync(PalabrasDbContext database, int userId, DateOnly today,
        DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        var todayKey = ReminderLogStore.Key(today);
        var claimed = await database.ReminderLogs.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Kind == ReminderLogStore.DailyKind
                && row.Channel == ReminderLogStore.WebPushChannel
                && row.Result == ReminderLogStore.SendingResult)
            .ToListAsync(cancellationToken);
        foreach (var row in claimed)
        {
            if (string.CompareOrdinal(row.LocalDate, todayKey) >= 0)
            {
                continue;
            }
            // Same rule the send loop uses at window close: a device that accepted the push makes the
            // day `sent`, an attempted device makes it `failed`, and a claim nothing was ever tried
            // for is `window_missed`. TTL and expiry are left as the claim stored them.
            var deliveries = await _claims.LoadDeliveriesAsync(row.Id, cancellationToken);
            var result = ReminderPlanner.FinalResult(deliveries);
            var recorded = await _claims.CompleteAsync(row.Id, result, ttlSeconds: null, expiresAtMs: null,
                nowLocal.ToUnixTimeMilliseconds(), cancellationToken);
            if (!recorded)
            {
                // Another tick finalised it between the read and this write; its decision stands.
                continue;
            }
            _logger.LogInformation(
                "Reminder for user {UserId} on {LocalDate} outlived its local day while still sending; " +
                "it closed as {Result} on the next tick.",
                userId, row.LocalDate, result);
        }
    }

    private async Task<ReminderLearnerOutcome> ClaimOrSuppressAsync(ReminderLearner learner, ReminderPlan plan,
        DayStatusResponse status, VapidKeys keys, DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        var nowMs = nowLocal.ToUnixTimeMilliseconds();
        switch (plan.Action)
        {
            case ReminderAction.Skip:
                if (string.Equals(plan.Reason, ReminderPlanner.InvalidSettingsReason, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Reminders for user {UserId} are skipped on {LocalDate}: the stored time is not a " +
                        "valid 00:00-23:45 value, so no window can be computed.",
                        learner.UserId, LearnDayStatus.Key(plan.LocalDate));
                }
                return ReminderLearnerOutcome.None;
            case ReminderAction.WindowMissed:
                if (!await _claims.SuppressAsync(learner.UserId, plan.LocalDate,
                        ReminderLogStore.WindowMissedResult, status.SkipAvailable, nowMs, cancellationToken))
                {
                    // Another tick wrote the day's decision between the read and this insert.
                    return new ReminderLearnerOutcome(true, false);
                }
                _logger.LogInformation(
                    "No reminder was sent to user {UserId} on {LocalDate}: the window closed at {WindowEnd} " +
                    "before any tick could run (the host was down for the whole window).",
                    learner.UserId, LearnDayStatus.Key(plan.LocalDate), LocalText(plan.WindowEnd));
                return new ReminderLearnerOutcome(true, false);
            case ReminderAction.Send:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Action, "Unexpected reminder action.");
        }

        var deliverable = learner.DeliverableSubscriptionIds(keys.KeyId);
        if (deliverable.Count == 0)
        {
            // Every active row is bound to a rotated-out VAPID key, so nothing can be signed for it.
            // One honest decision per day, healed only when the browser subscribes again (plan §2.2).
            if (!await _claims.SuppressAsync(learner.UserId, plan.LocalDate, ReminderLogStore.NoTargetResult,
                    status.SkipAvailable, nowMs, cancellationToken))
            {
                return new ReminderLearnerOutcome(true, false);
            }
            _logger.LogWarning(
                "No reminder was sent to user {UserId} on {LocalDate}: all {Count} subscription row(s) are " +
                "bound to another VAPID key and the browser has to re-subscribe.",
                learner.UserId, LearnDayStatus.Key(plan.LocalDate), learner.Subscriptions.Count);
            return new ReminderLearnerOutcome(true, false);
        }

        var claim = await _claims.ClaimAsync(learner.UserId, plan.LocalDate, status.SkipAvailable,
            plan.TtlSeconds, plan.ExpiresAtMs, deliverable, nowMs, cancellationToken);
        if (claim.Row.Result != ReminderLogStore.SendingResult)
        {
            // Another tick decided between the read and the claim; the row it wrote owns today.
            return new ReminderLearnerOutcome(true, false);
        }
        return await DeliverAsync(learner.UserId, claim.Row, plan, status, keys, nowLocal, cancellationToken);
    }

    private async Task<ReminderLearnerOutcome> DeliverAsync(int userId, ReminderLog row, ReminderPlan plan,
        DayStatusResponse status, VapidKeys keys, DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        var deliveries = await _claims.LoadDeliveriesAsync(row.Id, cancellationToken);
        var deliveredAny = deliveries.Any(delivery => delivery.Result == PushDeliveryResults.Sent);
        var dayClosed = false;
        foreach (var delivery in deliveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delivery.SubscriptionId is not { } subscriptionId)
            {
                continue;
            }
            // `sent`, `gone` and `rejected` are final for this device; a `pending` row means "never
            // attempted yet" and `transient` means "try again within the budget".
            if (delivery.Result is PushDeliveryResults.Sent or PushDeliveryResults.Gone
                or PushDeliveryResults.Rejected)
            {
                continue;
            }
            if (delivery.Attempts >= ReminderPlanner.MaximumAttemptsPerDevice)
            {
                continue;
            }
            if (nowLocal >= plan.WindowEnd)
            {
                break;
            }
            // Re-checked immediately before every attempt (plan §2.4): a session finished at 22:00:30
            // stops the retries for the devices that have not been sent to yet. Settings are
            // deliberately not re-read here: the claim is date-keyed and only done/skipped close a
            // day, so disabling or muting at 22:04 does not retract the 22:05 send (U6 review M2).
            if (await DayIsClosedAsync(userId, DateOnly.FromDateTime(nowLocal.DateTime),
                    cancellationToken))
            {
                dayClosed = true;
                break;
            }
            var message = Message(plan, status, nowLocal);
            try
            {
                var result = await _sender.SendAsync(subscriptionId, message, keys, row.Id, cancellationToken);
                if (result.Outcome == PushOutcome.Delivered)
                {
                    deliveredAny = true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A device that throws (a transport fault the sender did not classify) must not stop
                // the other device's reminder; the next tick retries it within the budget.
                _logger.LogError(exception,
                    "The reminder for user {UserId} could not be attempted on subscription {SubscriptionId}; " +
                    "the remaining devices continue.",
                    userId, subscriptionId);
            }
        }

        var refreshed = await _claims.LoadDeliveriesAsync(row.Id, cancellationToken);
        deliveredAny |= refreshed.Any(delivery => delivery.Result == PushDeliveryResults.Sent);
        var closed = dayClosed || nowLocal >= plan.WindowEnd;
        if (deliveredAny)
        {
            await CompleteAsync(row.Id, ReminderLogStore.SentResult, plan, userId, nowLocal, cancellationToken);
            return new ReminderLearnerOutcome(true, true);
        }
        if (!closed)
        {
            // The window is still open: the claim stays `sending` and the next minute tick resumes
            // whatever is left (a transient device, a device that has no budget left yet).
            return new ReminderLearnerOutcome(true, false);
        }
        var finalResult = ReminderPlanner.FinalResult(refreshed);
        await CompleteAsync(row.Id, finalResult, plan, userId, nowLocal, cancellationToken);
        return new ReminderLearnerOutcome(true, finalResult == ReminderLogStore.SentResult);
    }

    private async Task CompleteAsync(int reminderLogId, string result, ReminderPlan plan, int userId,
        DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        var recorded = await _claims.CompleteAsync(reminderLogId, result,
            plan.TtlSeconds, plan.ExpiresAtMs, nowLocal.ToUnixTimeMilliseconds(), cancellationToken);
        if (!recorded)
        {
            // Another tick finalised the same claim; its decision is the one that stands.
            _logger.LogInformation(
                "The reminder decision for user {UserId} on {LocalDate} was already finalised by another tick.",
                userId, LearnDayStatus.Key(plan.LocalDate));
            return;
        }
        _logger.LogInformation("Reminder for user {UserId} on {LocalDate} closed as {Result}.",
            userId, LearnDayStatus.Key(plan.LocalDate), result);
    }

    private async Task<bool> DayIsClosedAsync(int userId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var database = _database.CreateContext();
        var key = LearnDayStatus.Key(today);
        return await database.DayStatuses.AsNoTracking()
            .AnyAsync(row => row.UserId == userId && row.LocalDate == key
                && (row.Status == DayStatusValues.Done || row.Status == DayStatusValues.Skipped),
                cancellationToken);
    }

    // Plan §3.7: no username, no ids, no endpoint; camelCase like every other /api payload.
    private static PushOutgoingMessage Message(ReminderPlan plan, DayStatusResponse status,
        DateTimeOffset attemptLocal)
    {
        var payload = new ReminderPayload(
            1,
            ReminderLogStore.DailyKind,
            LearnDayStatus.Key(plan.LocalDate),
            LocalText(plan.WindowEnd),
            status.Streak,
            status.SkipAvailable,
            status.SkipNextAvailable,
            ReminderPayload.StartUrl);
        return new PushOutgoingMessage(JsonSerializer.Serialize(payload, PayloadJson),
            ReminderPlanner.TimeToLiveSeconds(plan.WindowEnd, attemptLocal), PushUrgency.Normal,
            Topic(plan.LocalDate));
    }

    private static string Topic(DateOnly localDate) =>
        ReminderLogStore.DailyKind + "-" + LearnDayStatus.Key(localDate);

    private static string LocalText(DateTimeOffset instant) => instant.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

// A learner with at least one active subscription and the devices that subscription set contains.
internal sealed record ReminderLearner(int UserId, IReadOnlyList<ReminderTarget> Subscriptions)
{
    // Only a row bound to the key this process signs with can be delivered to; the sender would
    // otherwise refuse it and mark it, so the tick filters first and never pretends to have a target.
    internal IReadOnlyList<int> DeliverableSubscriptionIds(string keyId) => Subscriptions
        .Where(subscription => string.Equals(subscription.VapidKeyId, keyId, StringComparison.Ordinal))
        .Select(subscription => subscription.Id)
        .ToList();
}

internal sealed record ReminderTarget(int Id, string VapidKeyId);

// What one learner's tick produced: a decision was written/resumed, and at least one device accepted
// the push.
internal readonly record struct ReminderLearnerOutcome(bool Decided, bool Delivered)
{
    internal static ReminderLearnerOutcome None => new(false, false);
}

// Per-tick totals, so tests can assert "one candidate, one decision" without reading logs.
internal readonly record struct ReminderTick(int Candidates, int Decisions, int Delivered)
{
    internal static ReminderTick None => new(0, 0, 0);
}

// The encrypted payload (plan §3.7). `expiresAt` is when the window closes: the service worker shows
// an expired `daily` push silently instead of ringing at 00:30.
internal sealed record ReminderPayload(int V, string Kind, string LocalDate, string ExpiresAt, int Streak,
    bool SkipAvailable, string? SkipNextAvailable, string Url)
{
    internal const string StartUrl = "/?start=1";
}
