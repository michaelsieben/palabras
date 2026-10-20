namespace Palabras.Persistence;

// M8 reminder decision log: at most one decision per learner-day, kind and channel
// (design-amendment.md ΔF, :642-647 and :224). The UNIQUE key is the claim that makes the sender
// once-per-day across restarts: INSERT wins, a duplicate means another tick already owns today.
// `result` stays mutable while a send is resumable: `sending` -> `sent`/`failed`/`gone`, and a
// claimed-but-never-attempted day is finalised as `window_missed`/`no_target`. The last transition
// out of `sending` wins; a decided row is never rewritten.
internal sealed class ReminderLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string LocalDate { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Result { get; set; } = "";
    public int? TtlS { get; set; }
    public long? ExpiresAtMs { get; set; }
    public bool SkipAvailable { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}
