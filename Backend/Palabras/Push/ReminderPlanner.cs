using System.Globalization;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Push;

// What the learner asked reminders to be (design-amendment.md ΔC.1 :196-202). "No row" means the
// plan's defaults, so the planner never needs to know whether a settings row exists.
internal sealed record ReminderPreferences(bool Enabled, string Time, string? MutedUntil, int WindowMinutes)
{
    internal static ReminderPreferences From(ReminderSettings? stored) => stored is null
        ? new ReminderPreferences(true, ReminderEndpoints.DefaultTime, null,
            ReminderEndpoints.DefaultWindowMinutes)
        : new ReminderPreferences(stored.Enabled, stored.ReminderTime, stored.MutedUntil,
            stored.SendWindowMin);
}

internal enum ReminderAction
{
    // Inside the window, nothing suppresses the day: claim it and deliver.
    Send,
    // Nothing to do, and nothing to record: disabled, muted, already done/skipped, a settings value
    // that cannot be used, or simply too early (the next minute tick decides again).
    Skip,
    // The window closed with no decision (the host was down for the whole window): record
    // `window_missed` instead of pretending a reminder was sent (ΔC.5 :273).
    WindowMissed
}

// One day's answer for one learner. `WindowEnd` is the local instant the send window closes
// (reminder time + window, clipped to 23:59) and is meaningful for every action: it is the TTL
// boundary the payload's `expiresAt` reports and the moment a late retry stops.
internal sealed record ReminderPlan(ReminderAction Action, string Reason, DateOnly LocalDate,
    DateTimeOffset WindowEnd, DateTimeOffset Now)
{
    internal int TtlSeconds => ReminderPlanner.TimeToLiveSeconds(WindowEnd, Now);

    internal long ExpiresAtMs => WindowEnd.ToUnixTimeMilliseconds();
}

// M8 U6: the whole "should this learner get a reminder today?" decision as a pure function of the
// stored settings, today's day_status and the learner's local clock — no database, no clock read,
// no logging, so every branch the minute tick can take is exhaustively testable.
//
// Plan §2.4 order: disabled/muted first (a learner absence), then a day that is already closed
// (done/skipped), then the window. A day that is done or skipped deliberately writes NO row, and a
// window that closed without any decision writes `window_missed` — never `sent`.
internal static class ReminderPlanner
{
    // RFC 8030 wants a TTL that outlives the useful window; 60 s is the floor the plan sets so a
    // delivery attempt at 23:29 still expires with the window rather than lingering into the night.
    internal const int MinimumTtlSeconds = 60;

    // D12: at most three attempts per device per day. The budget lives in push_deliveries.attempts,
    // not in a memory counter, so a restart resumes with the count it already spent.
    internal const int MaximumAttemptsPerDevice = 3;

    // The send window never runs past the end of the learner's local day: no 02:00 reminder even
    // when a stored time plus a long window would cross midnight.
    internal const string EndOfWindowTime = "23:59";

    internal const string InWindowReason = "in_window";
    internal const string WindowMissedReason = "window_missed";
    internal const string DisabledReason = "disabled";
    internal const string MutedReason = "muted";
    internal const string DoneReason = "day_done";
    internal const string SkippedReason = "day_skipped";
    internal const string BeforeWindowReason = "before_window";
    internal const string InvalidSettingsReason = "invalid_settings";

    // `nowLocal` is the same instant the M7 day-status code calls "today" (LearnerClock.LocalTimeOf),
    // so the planner, day_status and the payload can never disagree about which day or hour it is.
    internal static ReminderPlan Evaluate(ReminderPreferences preferences, string? todayStatus,
        DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var localDate = DateOnly.FromDateTime(nowLocal.DateTime);

        // A hand-edited or legacy row can carry a time the API would refuse (the frozen CHECK only
        // guards the length), so an unusable value falls back to the default for the window
        // arithmetic and suppresses the day instead of throwing inside a background tick.
        var timeIsUsable = ReminderEndpoints.TryParseTime(preferences.Time, out var time);
        if (!timeIsUsable)
        {
            time = ReminderEndpoints.DefaultTime;
        }
        var start = AtLocal(localDate, time, nowLocal.Offset);
        var windowEnd = EndOfWindow(localDate, start, preferences.WindowMinutes, nowLocal.Offset);

        if (!preferences.Enabled)
        {
            return new ReminderPlan(ReminderAction.Skip, DisabledReason, localDate, windowEnd, nowLocal);
        }
        if (IsMuted(preferences.MutedUntil, localDate))
        {
            return new ReminderPlan(ReminderAction.Skip, MutedReason, localDate, windowEnd, nowLocal);
        }
        if (!timeIsUsable)
        {
            return new ReminderPlan(ReminderAction.Skip, InvalidSettingsReason, localDate, windowEnd, nowLocal);
        }
        // Only done and skipped close a day; a `missed` day is history written by the M7 rollover for
        // a past date and never suppresses today.
        if (string.Equals(todayStatus, DayStatusValues.Done, StringComparison.Ordinal))
        {
            return new ReminderPlan(ReminderAction.Skip, DoneReason, localDate, windowEnd, nowLocal);
        }
        if (string.Equals(todayStatus, DayStatusValues.Skipped, StringComparison.Ordinal))
        {
            return new ReminderPlan(ReminderAction.Skip, SkippedReason, localDate, windowEnd, nowLocal);
        }
        if (nowLocal < start)
        {
            return new ReminderPlan(ReminderAction.Skip, BeforeWindowReason, localDate, windowEnd, nowLocal);
        }
        // The window is half-open: the reminder time itself is inside it, windowEnd is not.
        if (nowLocal >= windowEnd)
        {
            return new ReminderPlan(ReminderAction.WindowMissed, WindowMissedReason, localDate, windowEnd,
                nowLocal);
        }
        return new ReminderPlan(ReminderAction.Send, InWindowReason, localDate, windowEnd, nowLocal);
    }

    // TTL = max(60, seconds until the window closes), recomputed per attempt so a delivery at 23:29
    // is not given the TTL a delivery at 22:00 was (plan §3.7).
    internal static int TimeToLiveSeconds(DateTimeOffset windowEnd, DateTimeOffset now) =>
        Math.Max(MinimumTtlSeconds, (int)Math.Floor((windowEnd - now).TotalSeconds));

    // How a claimed day is closed once the window is over (or the learner closed the day under the
    // send loop). Plan §2.4: any `sent` wins, an all-gone set is `gone`, and a day we actually
    // attempted but never landed is `failed`. Nothing attempted at all (a crash between the claim and
    // the first send) is `window_missed`: the frozen CHECK has no "cancelled" value, so it is the
    // only honest one that says "no notification went out".
    internal static string FinalResult(IReadOnlyList<ReminderDelivery> deliveries)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        if (deliveries.Any(delivery => delivery.Result == PushDeliveryResults.Sent))
        {
            return ReminderLogStore.SentResult;
        }
        if (deliveries.Count > 0 && deliveries.All(delivery => delivery.Result == PushDeliveryResults.Gone))
        {
            return ReminderLogStore.GoneResult;
        }
        if (deliveries.Any(delivery => delivery.Attempts > 0))
        {
            return ReminderLogStore.FailedResult;
        }
        return ReminderLogStore.WindowMissedResult;
    }

    // `muted_until` is inclusive (plan §2.3): a mute "until today" silences today and resumes
    // tomorrow. A value that is not a date is ignored rather than silencing the learner forever.
    private static bool IsMuted(string? mutedUntil, DateOnly today) =>
        LearnDayStatus.TryParseLocalDate(mutedUntil, out var muted) && muted >= today;

    // The window end is built from the same zone offset as `start` — the offset of the tick's own
    // instant — so `nowLocal`, `start` and `windowEnd` all carry one offset and it cancels in every
    // comparison: the decision is pure local wall clock. That is what keeps a window that really does
    // cross the European 02:00/03:00 DST transition honest (a 01:30 + 90 min window sends only in the
    // first 01:30-01:59 hour in spring and across both 01:30+02:00 and 02:30+01:00 in autumn). The
    // 23:59 clip exists only to keep the window inside the learner's local day; it is NOT what avoids
    // the DST transition, which a window starting before 02:45 still crosses.
    private static DateTimeOffset EndOfWindow(DateOnly date, DateTimeOffset start, int windowMinutes,
        TimeSpan offset)
    {
        var minutes = Math.Clamp(windowMinutes, ReminderEndpoints.MinimumWindowMinutes,
            ReminderEndpoints.MaximumWindowMinutes);
        var configuredEnd = start.AddMinutes(minutes);
        var endOfDay = AtLocal(date, EndOfWindowTime, offset);
        return configuredEnd < endOfDay ? configuredEnd : endOfDay;
    }

    private static DateTimeOffset AtLocal(DateOnly date, string time, TimeSpan offset)
    {
        var clock = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(date.ToDateTime(clock, DateTimeKind.Unspecified), offset);
    }
}
