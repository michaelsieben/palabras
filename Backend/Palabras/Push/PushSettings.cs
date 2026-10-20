using Palabras.Persistence;

namespace Palabras.Push;

// M8 push rollout configuration. Push ships default-OFF on exactly the M7 rollover-gate rule
// (DayRolloverSettings): only a literal `true` (trimmed, case-insensitive) enables it, so an
// absent, empty, "1", "yes" or garbage value can never start the feature by accident. There is
// no second switch and no per-request override: the container setting is Push__Enabled.
//
// The VAPID key file deliberately lives beside the database (never in it): restoring an older
// database snapshot must not bring back an older key and orphan every live subscription
// (plan §2.2, D1). Default path: <database directory>/vapid.json.
internal sealed record PushSettings(bool Enabled, string VapidKeyPath, string Subject)
{
    internal const string EnabledKey = "Push:Enabled";
    internal const string VapidKeyPathKey = "Push:VapidKeyPath";
    internal const string SubjectKey = "Push:Subject";
    // RFC 8292 accepts an https URL as the VAPID subject; this avoids committing an e-mail.
    internal const string DefaultSubject = "https://palabras.siebentower.nl";

    internal static PushSettings FromConfiguration(IConfiguration configuration, DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(database);
        var enabled = bool.TryParse(configuration[EnabledKey]?.Trim(), out var parsed) && parsed;
        var configuredPath = configuration[VapidKeyPathKey];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Path.GetDirectoryName(database.FilePath)!, "vapid.json")
            : Path.GetFullPath(configuredPath.Trim());
        var subject = configuration[SubjectKey];
        return new PushSettings(enabled, path,
            string.IsNullOrWhiteSpace(subject) ? DefaultSubject : subject.Trim());
    }
}
