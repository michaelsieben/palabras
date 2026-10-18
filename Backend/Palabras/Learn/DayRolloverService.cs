using Palabras.Persistence;

namespace Palabras.Learn;

// M7 rollover tick (ΔD:297). Default-OFF behind DayRolloverSettings (T-20261018-2105 C1): with
// the gate off this service logs one line at startup and never schedules a tick, so a fresh
// deploy migrates the schema but writes no missed history. The first tick deliberately waits
// one interval when enabled: on startup the service must not decide yesterday before learning
// whether a session completed (and tests keep their own clock). The next tick after a restart
// is at most a minute later, which is well inside the window the spec cares about.
internal sealed class DayRolloverService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private readonly DatabaseSettings _database;
    private readonly DayRolloverSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DayRolloverService> _logger;

    // The enable instant is fixed when the service is constructed (host start). DayRollover
    // then refuses any candidate day that began before it: yesterday relative to the first
    // enabled tick routinely predates the feature, and the client could not have reported a
    // completion for a day that was already half over — those days must stay unwritten
    // (T-20261018-2105 C1 backfill guard, motivating the C4 cutover date).
    public DayRolloverService(DatabaseSettings database, DayRolloverSettings settings, TimeProvider time,
        ILogger<DayRolloverService> logger)
    {
        _database = database;
        _settings = settings;
        _time = time;
        _logger = logger;
        TrackingStart = settings.Enabled ? time.GetUtcNow() : default;
    }

    internal DateTimeOffset TrackingStart { get; }

    internal bool Enabled => _settings.Enabled;

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
        _logger.LogInformation("Day rollover is enabled; missed days are written only for local days that " +
            "began at or after {TrackingStart}.", TrackingStart);
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
    internal Task<int> RunTickAsync(CancellationToken cancellationToken) =>
        _settings.Enabled
            ? DayRollover.RunAsync(_database, _time, _logger, TrackingStart, cancellationToken)
            : Task.FromResult(0);
}
