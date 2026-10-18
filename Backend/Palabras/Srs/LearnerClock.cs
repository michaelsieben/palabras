namespace Palabras.Srs;

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

    // Inclusive millisecond boundary, matching the persisted queue instants and SQL <= predicate.
    public DateTimeOffset EndOfLocalDayUtc => StartOfLocalDateUtc(LocalToday.AddDays(1)).AddMilliseconds(-1);

    public DateOnly LocalDateOf(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _zone).DateTime);

    public DateTimeOffset ClampReviewedAt(DateTimeOffset reviewedAt)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset earliest = now.AddHours(-24);
        DateTimeOffset latest = now.AddMinutes(5);
        return (reviewedAt < earliest ? earliest : reviewedAt > latest ? latest : reviewedAt).ToUniversalTime();
    }

    private DateTimeOffset StartOfLocalDateUtc(DateOnly date)
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
