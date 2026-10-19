namespace Palabras.Persistence;

// M8 reminder settings (design-amendment.md ΔC.1, :196-202). Deliberately its own table and not new
// `user_settings` columns: a CHECK on an existing table costs a rebuild (docs/08-upgrade-rollback.md
// §G), while a table with its own constraints is purely additive. No row means "defaults", so a
// learner who never opened the reminder panel keeps 22:00 / a 90-minute window / enabled.
internal sealed class ReminderSettings
{
    public int UserId { get; set; }
    public bool Enabled { get; set; } = true;
    public string ReminderTime { get; set; } = "22:00";
    public string? MutedUntil { get; set; }
    public int SendWindowMin { get; set; } = 90;
    public long UpdatedAtMs { get; set; }
}
