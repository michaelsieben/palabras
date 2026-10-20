namespace Palabras.Srs;

// The only place that turns UTC into the learner's local day (plan §3, zone = users.timezone ??
// "Europe/Madrid"). Additive read-only helpers added in S7 for the plan/forecast windows:
// - StartOfLocalDayUtc / EndOfLocalDayUtc: "today" and its inclusive end, i.e. they read the
//   injected TimeProvider exactly like LocalToday does (never DateTimeOffset.UtcNow).
// - StartOfLocalDateUtc(DateOnly) / EndOfLocalDateUtc(DateOnly): pure functions of the zone and
//   the supplied date. They read no clock and never mutate state, so callers can ask about
//   tomorrow, a forecast day or a DST edge without inventing a second conversion.
internal sealed class LearnerClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _zone;

    public LearnerClock(TimeProvider timeProvider, string? ianaZone)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _zone = TimeZoneInfo.FindSystemTimeZoneById(ianaZone ?? "Europe/Madrid");
    }

    public DateOnly LocalToday => LocalDateOf(_timeProvider.GetUtcNow());

    public DateTimeOffset StartOfLocalDayUtc => StartOfLocalDateUtc(LocalToday);

    // Inclusive millisecond boundary, matching the persisted queue instants and SQL <= predicate.
    public DateTimeOffset EndOfLocalDayUtc => EndOfLocalDateUtc(LocalToday);

    public DateOnly LocalDateOf(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _zone).DateTime);

    // The local wall-clock instant of a UTC instant: what the M8 reminder planner compares a stored
    // reminder time against. A pure function of the supplied instant, exactly like LocalDateOf, so a
    // DST day reports the offset in effect at that instant and no second conversion exists.
    public DateTimeOffset LocalTimeOf(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _zone);

    public DateTimeOffset ClampReviewedAt(DateTimeOffset reviewedAt)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset earliest = now.AddHours(-24);
        DateTimeOffset latest = now.AddMinutes(5);
        return (reviewedAt < earliest ? earliest : reviewedAt > latest ? latest : reviewedAt).ToUniversalTime();
    }

    public DateTimeOffset EndOfLocalDateUtc(DateOnly date) => StartOfLocalDateUtc(date.AddDays(1)).AddMilliseconds(-1);

    public DateTimeOffset StartOfLocalDateUtc(DateOnly date)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some IANA zones advance at midnight: use the first valid instant of that local day.
        while (_zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }
        if (_zone.IsAmbiguousTime(local))
        {
            // First occurrence of midnight is the day boundary (larger offset means earlier UTC).
            return new DateTimeOffset(local, _zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone));
    }
}
