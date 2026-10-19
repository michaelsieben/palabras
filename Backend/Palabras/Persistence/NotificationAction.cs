namespace Palabras.Persistence;

// M8 notification action audit (design-amendment.md :649-655). Append-only: the service worker and
// the app post what the learner did with a reminder, and nothing reads it back for state yet. A
// missing `reminder_log_id` means the action arrived for a notification this server no longer has a
// decision row for; the audit row itself must still be kept.
internal sealed class NotificationAction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? ReminderLogId { get; set; }
    public string LocalDate { get; set; } = "";
    public string Action { get; set; } = "";
    public string Result { get; set; } = "";
    public string? MuteChoice { get; set; }
    public long CreatedAtMs { get; set; }
}
