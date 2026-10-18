namespace Palabras.Persistence;

// Smallest durable store for application-wide markers: one TEXT row per key, and each key
// chooses its own value encoding. M7 uses it to fix the day-rollover cutover instant once so a
// restart or redeploy can never move it (T-20261018-2105 step 11). The whole database file is
// snapshotted by the nightly backup, so a restored snapshot carries its own cutover.
internal sealed class AppMeta
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
