using Palabras.Persistence;

namespace Palabras.Learn;

// M7 rollover tick (ΔD:297). The first tick deliberately waits one interval: on startup the
// service must not decide yesterday before learning whether a session completed (and tests keep
// their own clock). The next tick after a restart is at most a minute later, which is well
// inside the window the spec cares about.
internal sealed class DayRolloverService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private readonly DatabaseSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DayRolloverService> _logger;

    public DayRolloverService(DatabaseSettings settings, TimeProvider time, ILogger<DayRolloverService> logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                await DayRollover.RunAsync(_settings, _time, _logger, stoppingToken);
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
}
