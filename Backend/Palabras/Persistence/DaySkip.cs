namespace Palabras.Persistence;

// M7 skip ledger, design-amendment.md ΔF:635-640. A row is a consumed skip until
// `refunded_at_ms` is set; the rolling 7-day window only counts non-refunded rows (ΔD:282,293).
internal sealed class DaySkip
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string LocalDate { get; set; } = "";
    public string Source { get; set; } = "";
    public long CreatedAtMs { get; set; }
    public long? RefundedAtMs { get; set; }
}
