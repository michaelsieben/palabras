namespace Palabras.Learn;

// M7 rollout gate (T-20261018-2105 C1). A fresh deploy runs the schema migration but writes no
// missed-day history: DayRolloverService only ticks when M7:DayRolloverEnabled is literally true
// (case-insensitive "true"). Anything else — absent, empty, "false", "1", garbage — stays OFF,
// so the safe state needs no opt-in. In the container the setting is the environment variable
// M7__DayRolloverEnabled. Kill switch: set it false (or remove it) and restart/recreate the app
// — no rebuild, no DB downgrade; past ticks stop, the migration and its rows stay.
// Bound to the same explicit static-binder convention as DatabaseSettings.FromConfiguration.
internal sealed record DayRolloverSettings(bool Enabled)
{
    internal const string EnabledKey = "M7:DayRolloverEnabled";

    internal static DayRolloverSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new DayRolloverSettings(bool.TryParse(configuration[EnabledKey]?.Trim(), out var enabled) && enabled);
    }
}
