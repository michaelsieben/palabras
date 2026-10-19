namespace Palabras.Persistence;

// M8 per-device delivery attempt for one reminder decision. Splitting deliveries out of
// `reminder_log` keeps the once-per-day UNIQUE on one row while a second device or a retry stays
// resumable. `subscription_id` is SET NULL when the push service reports 404/410 and the
// subscription row is deleted, so the attempt history survives the device.
internal sealed class PushDelivery
{
    public int Id { get; set; }
    public int ReminderLogId { get; set; }
    public int? SubscriptionId { get; set; }
    public int Attempts { get; set; }
    public string Result { get; set; } = "";
    public int? LastStatus { get; set; }
    public long UpdatedAtMs { get; set; }
}
