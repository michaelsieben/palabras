using Palabras.Persistence;

namespace Palabras.Learn;

// M7 rollover tick (ΔD:297). Default-OFF behind DayRolloverSettings (T-20261018-2105 C1): with
// the gate off this service logs one line at startup and never schedules a tick, so a fresh
// deploy migrates the schema but writes no missed-day history. The first tick deliberately waits
// one interval when enabled: on startup the service must not decide yesterday before learning
// whether a session completed (and tests keep their own clock). The next tick after a restart
// is at most a minute later, which is well inside the window the spec cares about.
//
// The cutover itself is durable, not the host-start instant (T-20261018-2105 step 11): the first
// enabled observation records it once in app_meta and every later tick, restart, redeploy or
// re-enable reads that same instant back. A restart can therefore no longer move the cutover
// forward and silently strand the day under way without a history row.
internal sealed class DayRolloverService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private readonly DatabaseSettings _database;
    private readonly DayRolloverSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DayRolloverService> _logger;

    // One resolution is shared by the startup path and every tick so the recorded cutover is the
    // instant of the first enabled observation, whichever call observes it first. The lock guards
    // only the cached-task field; the database work runs unlocked.
    private readonly object _cutoverGate = new();
    private Task<DateTimeOffset>? _cutoverTask;

    public DayRolloverService(DatabaseSettings database, DayRolloverSettings settings, TimeProvider time,
        ILogger<DayRolloverService> logger)
    {
        _database = database;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    internal bool Enabled => _settings.Enabled;

    // The durable cutover instant: read from app_meta, or — when this database has never observed
    // the gate enabled — fixed to this instant and persisted before any day is judged. A failed
    // resolution is un-cached so a later tick retries; the worst outcome of a concurrent failed
    // retry is a second EnsureCutoverAsync whose PK-conflict path adopts the stored value, so the
    // recorded instant stays the first successfully persisted one.
    internal Task<DateTimeOffset> CutoverAsync(CancellationToken cancellationToken)
    {
        lock (_cutoverGate)
        {
            return _cutoverTask ??= ResolveCutoverAsync(cancellationToken);
        }
    }

    private async Task<DateTimeOffset> ResolveCutoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await DayRollover.EnsureCutoverAsync(_database, _time, _logger, cancellationToken);
        }
        catch
        {
            lock (_cutoverGate)
            {
                _cutoverTask = null;
            }
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation(
                "Day rollover is disabled ({Setting} is not true): the schema is migrated but no missed-day " +
                "history is written. Set it to true and restart to enable the tracking cutover.",
                DayRolloverSettings.EnabledKey);
            return;
        }
        try
        {
            var cutover = await CutoverAsync(stoppingToken);
            _logger.LogInformation("Day rollover is enabled; missed days are written only for local days that " +
                "began at or after the persisted cutover {Cutover}.", cutover);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // A failed resolution must not kill the host; the next tick retries it.
            _logger.LogError(exception, "Day rollover could not read or record its cutover; it retries on the " +
                "next minute tick.");
        }
        using var timer = new PeriodicTimer(Tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never swallow cancellation; a failed tick is retried on the next one.
                _logger.LogError(exception, "Day rollover failed; it retries on the next minute tick.");
            }
        }
    }

    // The tick body, split out so integration tests drive one tick without waiting a real
    // minute. With the gate off it is a no-op that never touches the database.
    internal async Task<int> RunTickAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return 0;
        }
        var cutover = await CutoverAsync(cancellationToken);
        return await DayRollover.RunAsync(_database, _time, _logger, cutover, cancellationToken);
    }
}
