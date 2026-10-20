using Microsoft.EntityFrameworkCore;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Push;

// M8 reminder settings, the mute shortcuts and the notification action audit (plan §3.5/§3.6,
// design-amendment.md ΔC.1 :196-202 and ΔC.3.1 :249-257). Learner-only and scoped by the cookie's
// user id, exactly like every other /api/learn route.
//
// Settings live in the frozen `reminder_settings` table and "no row" means the plan's defaults, so
// GET never writes: a learner who never opened the panel reads 22:00 / 90 minutes / enabled without
// a database row appearing. PUT is an upsert of the merged result, because a partial body (only the
// time, or only a mute shortcut) must not silently reset the fields it omitted.
//
// Settings are a preference, not delivery: GET and PUT answer 200 even while push is off or the
// VAPID key file is unusable, so a first deploy can configure 21:30 before enabling push and a key
// fault can never hide what is stored. Only the action audit stays behind the 503 gate — it is
// produced by real notifications, and keeping junk out of `notification_actions` is worth it.
internal static class ReminderEndpoints
{
    private const string Policy = "learner";
    internal const string DefaultTime = "22:00";
    internal const int DefaultWindowMinutes = 90;
    internal const int MinimumWindowMinutes = 15;
    internal const int MaximumWindowMinutes = 240;
    // D6: the picker is 00:00-23:45 in 15-minute steps, so a window never has to start after
    // 23:45 to still end inside the same local day (it is clipped at 23:59 by the sender).
    internal const int TimeStepMinutes = 15;
    // A mute is a bounded absence, not a way to switch reminders off for a season; "disable" is
    // the one-tap permanent answer.
    internal const int MaximumMuteDays = 30;

    private static readonly string[] ActionKinds = ["open", "skip", "mute"];
    private static readonly string[] ActionResultValues =
        ["ok", "skip_unavailable", "already_done", "unauthenticated", "error"];
    // The frozen CHECK admits six choices; four of them ("tomorrow", "3d", "7d", "disable") are
    // also the PUT shortcuts that change the stored date, the other two only describe an action.
    private static readonly string[] ActionMuteChoices = ["tomorrow", "3d", "7d", "disable", "time_changed", "cancel"];

    internal static void MapReminderEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/learn").RequireAuthorization(Policy);
        group.MapGet("/reminder", GetAsync);
        group.MapPut("/reminder", PutAsync);
        group.MapPost("/notification-action", NotificationActionAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext context, DatabaseSettings settings, VapidKeyStore keys,
        CancellationToken cancellationToken)
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
        var stored = await database.ReminderSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        return Results.Ok(await ComposeAsync(database, userId, stored, keys, cancellationToken));
    }

    private static async Task<IResult> PutAsync(ReminderSettingsRequest? request, HttpContext context,
        DatabaseSettings settings, TimeProvider timeProvider, ILoggerFactory loggerFactory, VapidKeyStore keys,
        CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "missing_body");
        }
        await using var database = settings.CreateContext();
        var learner = await LearnEndpoints.LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = LearnEndpoints.CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var today = clock.LocalToday;

        // The row is keyed by user_id, and one owner account can have the panel open on two
        // devices: reading and writing under the immediate transaction (same helper as the
        // subscription upsert) makes the second request adopt the first one's row instead of
        // failing on the primary key.
        await using var transaction = await PushEndpoints.PushWriteTransaction.BeginAsync(database,
            cancellationToken);
        var stored = await database.ReminderSettings.SingleOrDefaultAsync(row => row.UserId == userId,
            cancellationToken);
        var enabled = request.Enabled ?? stored?.Enabled ?? true;
        var time = stored?.ReminderTime ?? DefaultTime;
        var windowMin = request.WindowMin ?? stored?.SendWindowMin ?? DefaultWindowMinutes;
        var mutedUntil = stored?.MutedUntil;

        // Mute shortcuts are exclusive: they compute the date from the learner's local today, and
        // mixing them with an explicit date or an enabled flag would make the request ambiguous.
        // "cancel" is the explicit un-mute (ΔC.3.1 has no un-mute action, but clearing a mute is one
        // preference write, not a disable/re-enable dance that could leave reminders off).
        if (request.MuteChoice is not null)
        {
            var choice = request.MuteChoice.Trim();
            if (request.Enabled is not null || request.MutedUntil is not null)
            {
                return Error(StatusCodes.Status400BadRequest, "conflicting_mute_choice");
            }
            switch (choice)
            {
                case "tomorrow":
                    mutedUntil = LearnDayStatus.Key(today.AddDays(1));
                    break;
                case "3d":
                    mutedUntil = LearnDayStatus.Key(today.AddDays(3));
                    break;
                case "7d":
                    mutedUntil = LearnDayStatus.Key(today.AddDays(7));
                    break;
                case "disable":
                    enabled = false;
                    // Disabling supersedes a mute that would only have paused today.
                    mutedUntil = null;
                    break;
                case "cancel":
                    // Un-mute without touching `enabled`: the action audit's own vocabulary.
                    mutedUntil = null;
                    break;
                default:
                    return Error(StatusCodes.Status400BadRequest, "invalid_mute_choice");
            }
        }

        if (request.Time is not null)
        {
            if (!TryParseTime(request.Time, out var normalized))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_time");
            }
            time = normalized;
        }
        if (request.WindowMin is { } requestedWindow)
        {
            if (requestedWindow is < MinimumWindowMinutes or > MaximumWindowMinutes)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_window");
            }
            windowMin = requestedWindow;
        }
        // An explicit date is the only way to shorten or move a mute; omitting it keeps the stored
        // one, so a partial PUT can never un-mute by accident. A date that is already past is
        // refused instead of being stored as a mute that means nothing.
        if (request.MutedUntil is not null)
        {
            if (!LearnDayStatus.TryParseLocalDate(request.MutedUntil, out var parsed)
                || parsed < today || parsed > today.AddDays(MaximumMuteDays))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_muted_until");
            }
            mutedUntil = LearnDayStatus.Key(parsed);
        }

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (stored is null)
        {
            stored = new ReminderSettings { UserId = userId };
            database.ReminderSettings.Add(stored);
        }
        stored.Enabled = enabled;
        stored.ReminderTime = time;
        stored.MutedUntil = mutedUntil;
        stored.SendWindowMin = windowMin;
        stored.UpdatedAtMs = nowMs;
        await database.SaveChangesAsync(cancellationToken);
        // Composed before the commit: the context's transaction is finished after CommitAsync, and
        // a query on a committed raw transaction is not guaranteed to still be associated.
        var response = await ComposeAsync(database, userId, stored, keys, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }

    // Fire-and-forget audit (plan §3.6): the service worker and the app post what the learner did
    // with a notification and nothing here ever changes reminder state — a lost row must not lose
    // the action. `reminderLogId` is best effort: an action can arrive for a notification whose
    // decision row this server no longer has, and the audit row is still worth keeping.
    private static async Task<IResult> NotificationActionAsync(NotificationActionRequest? request,
        HttpContext context, DatabaseSettings settings, TimeProvider timeProvider, ILoggerFactory loggerFactory,
        VapidKeyStore keys, CancellationToken cancellationToken)
    {
        if (PushEndpoints.Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "missing_body");
        }
        if (!LearnDayStatus.TryParseLocalDate(request.LocalDate, out var localDate))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_local_date");
        }
        if (request.Action is null || Array.IndexOf(ActionKinds, request.Action.Trim()) < 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_action");
        }
        if (request.Result is null || Array.IndexOf(ActionResultValues, request.Result.Trim()) < 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_result");
        }
        var muteChoice = request.MuteChoice?.Trim();
        if (muteChoice is not null && Array.IndexOf(ActionMuteChoices, muteChoice) < 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_mute_choice");
        }
        await using var database = settings.CreateContext();
        var learner = await LearnEndpoints.LoadLearnerAsync(database, userId, cancellationToken);
        if (learner is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var clock = LearnEndpoints.CreateClock(timeProvider, learner.Timezone, loggerFactory);
        var today = clock.LocalToday;
        // A tap can legitimately arrive the morning after the notification (the skip answer "ese
        // día ya pasó"), so yesterday is still accepted; anything older or in the future would only
        // be junk in the audit table.
        if (localDate > today || localDate < today.AddDays(-1))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_local_date");
        }
        var decision = await ReminderLogStore.FindAsync(database, userId, localDate, cancellationToken);
        database.NotificationActions.Add(new NotificationAction
        {
            UserId = userId,
            ReminderLogId = decision?.Id,
            LocalDate = LearnDayStatus.Key(localDate),
            Action = request.Action.Trim(),
            Result = request.Result.Trim(),
            MuteChoice = muteChoice,
            CreatedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        });
        await database.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<ReminderResponse> ComposeAsync(PalabrasDbContext database, int userId,
        ReminderSettings? stored, VapidKeyStore keys, CancellationToken cancellationToken)
    {
        // Counted by state, never by last_status: a fresh row and a healed row both have a NULL
        // status (U4 review ruling 3), and only `state` answers "will this device ring". The key
        // fingerprint is part of the answer too: a row bound to a rotated-out VAPID key is still
        // `active` but the sender refuses it, so counting it would claim a device that cannot ring.
        // A null key store (push off, or an unusable key file) therefore counts nothing.
        var currentKeyId = keys.Keys?.KeyId;
        var activeDevices = currentKeyId is null
            ? 0
            : await database.PushSubscriptions.AsNoTracking()
                .CountAsync(row => row.UserId == userId && row.State == PushSubscriptionLifecycle.Active
                    && row.VapidKeyId == currentKeyId, cancellationToken);
        return new ReminderResponse(stored?.Enabled ?? true, stored?.ReminderTime ?? DefaultTime,
            stored?.MutedUntil, stored?.SendWindowMin ?? DefaultWindowMinutes, activeDevices);
    }

    // "HH:mm" in 00:00-23:45, 15-minute steps (D6). Strict two-digit parsing: "7:00" is a client
    // bug, not a time to guess at, and 23:45 is the last value because the next step is midnight.
    // The minute bound is explicit: `22:60` is not a time, and without it the 15-minute check alone
    // would accept and store it (the frozen CHECK only requires length = 5).
    internal static bool TryParseTime(string? raw, out string normalized)
    {
        normalized = "";
        var value = raw?.Trim();
        if (value is not { Length: 5 } || value[2] != ':')
        {
            return false;
        }
        if (!char.IsAsciiDigit(value[0]) || !char.IsAsciiDigit(value[1])
            || !char.IsAsciiDigit(value[3]) || !char.IsAsciiDigit(value[4]))
        {
            return false;
        }
        var hour = ((value[0] - '0') * 10) + (value[1] - '0');
        var minute = ((value[3] - '0') * 10) + (value[4] - '0');
        if (hour > 23 || minute > 59 || minute % TimeStepMinutes != 0)
        {
            return false;
        }
        normalized = value;
        return true;
    }

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);
}

// Response shape of GET and PUT /api/learn/reminder (plan §3.5). `activeDevices` is a fact, not a
// permission: the SPA uses it to tell "reminders on, nothing subscribed" from "device removed".
internal sealed record ReminderResponse(bool Enabled, string Time, string? MutedUntil, int WindowMin,
    int ActiveDevices);

// Body of PUT /api/learn/reminder. Every field is optional and an absent field keeps its stored
// value; `muteChoice` is exclusive and computes `mutedUntil` from the learner's local today
// (tomorrow|3d|7d), clears it (cancel) or disables reminders (disable).
internal sealed record ReminderSettingsRequest(bool? Enabled, string? Time, int? WindowMin, string? MutedUntil,
    string? MuteChoice);

// Body of POST /api/learn/notification-action (plan §3.6). `localDate` is the day the notification
// was about, in the learner's zone; it is not derived from the request time so a tap after local
// midnight still names the day the learner saw.
internal sealed record NotificationActionRequest(string? LocalDate, string? Action, string? Result,
    string? MuteChoice);
