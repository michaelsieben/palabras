namespace Palabras.Push;

// Plan §3.4 asks for at most one test send per learner per 30 seconds. The limit is per user, not
// per endpoint, because its purpose is "do not let one click fan out to every device", and it is
// kept in memory: losing it on restart costs one extra test notification, while a database column
// for it would need a migration the frozen U1 schema does not have. The clock is the app's single
// TimeProvider so the window is testable without sleeping.
internal sealed class PushTestCooldown
{
    internal const int WindowSeconds = 30;

    private readonly TimeProvider _time;
    private readonly Dictionary<int, DateTimeOffset> _lastSendAt = [];
    private readonly object _gate = new();

    public PushTestCooldown(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    // Check and claim in one step, so two concurrent clicks cannot both pass.
    internal bool TryAcquire(int userId, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastSendAt.TryGetValue(userId, out var last))
            {
                var elapsed = now - last;
                if (elapsed < TimeSpan.FromSeconds(WindowSeconds))
                {
                    retryAfter = TimeSpan.FromSeconds(WindowSeconds) - elapsed;
                    return false;
                }
            }
            _lastSendAt[userId] = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
