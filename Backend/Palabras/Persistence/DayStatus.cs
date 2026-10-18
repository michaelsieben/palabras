namespace Palabras.Persistence;

// M7 day record. One row per learner per local date, PK (user_id, local_date) per
// design.md:117-120; the `frozen` status and `iso_week` column are removed by
// design-amendment.md ΔF:631-633. `decided_at_ms` is a UTC unix-ms instant, matching the
// repo-wide instant convention (07-learn-api.md:175).
internal sealed class DayStatus
{
    public int UserId { get; set; }
    public string LocalDate { get; set; } = "";
    public string Status { get; set; } = "";
    public int? SessionId { get; set; }
    public long DecidedAtMs { get; set; }
}
