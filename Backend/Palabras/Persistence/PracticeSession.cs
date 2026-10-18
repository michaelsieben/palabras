namespace Palabras.Persistence;

// M7 session record, design.md:103-106. M7 needs it to answer "was the composed session
// completed" for the `done` day status (design.md:171) and to point day_status.session_id at
// something. Instants are UTC unix milliseconds, matching the repo convention.
internal sealed class PracticeSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string LocalDate { get; set; } = "";
    public long StartedAtMs { get; set; }
    public long? CompletedAtMs { get; set; }
    public int? DurationS { get; set; }
    public int? PlannedS { get; set; }
    public int NewCount { get; set; }
    public int ReviewCount { get; set; }
    public string Status { get; set; } = "";
}
