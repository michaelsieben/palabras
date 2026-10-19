namespace Palabras.Persistence;

// M8 Web Push device registration (design-amendment.md ΔC.2, :204-224). One row per browser push
// subscription, keyed by the push service endpoint. `vapid_key_id` binds the row to the VAPID key it
// was created under: a rotated key makes the sender mark the row `stale_key` instead of attempting a
// delivery the push service would reject. Instants are UTC unix milliseconds, the repo convention.
internal sealed class PushSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string VapidKeyId { get; set; } = "";
    public string? DeviceLabel { get; set; }
    public string State { get; set; } = "active";
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public long? LastSuccessAtMs { get; set; }
    public long? LastFailureAtMs { get; set; }
    public int FailureCount { get; set; }
    public int? LastStatus { get; set; }
}
