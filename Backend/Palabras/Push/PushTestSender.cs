using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Push;

// The "Enviar prueba" path (plan §3.4, §3.7): the fixed test payload to the caller's deliverable
// devices, plus the `push test-send` verb behind it. Shared by the route and the CLI so both apply
// exactly the same selection rules, and there is no second copy of the payload or TTL.
internal sealed class PushTestSender
{
    internal const int TimeToLiveSeconds = 60;
    internal static readonly PushOutgoingMessage Message =
        new("{\"v\":1,\"kind\":\"test\"}", TimeToLiveSeconds, PushUrgency.High, null);

    private readonly DatabaseSettings _settings;
    private readonly VapidKeyStore _keys;
    private readonly PushSender _sender;

    public PushTestSender(DatabaseSettings settings, VapidKeyStore keys, PushSender sender)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sender);
        _settings = settings;
        _keys = keys;
        _sender = sender;
    }

    // Only `active` rows are ever sent to, and a named endpoint that is not one of them is reported
    // as absent rather than guessed at (plan §3.8, review item b).
    internal async Task<IReadOnlyList<PushTestTarget>> ResolveTargetsAsync(int? userId, string? endpoint,
        CancellationToken cancellationToken)
    {
        await using var database = _settings.CreateContext();
        return await QueryAsync(database, userId, endpoint, cancellationToken);
    }

    // Shared with `push test-send` so the route and the CLI cannot disagree about which devices
    // exist. Stale-key rows are still selected: the sender marks them instead of sending.
    internal static async Task<IReadOnlyList<PushTestTarget>> QueryAsync(PalabrasDbContext database, int? userId,
        string? endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var query = database.PushSubscriptions.AsNoTracking()
            .Where(row => row.State == PushSubscriptionLifecycle.Active);
        if (userId is { } id)
        {
            query = query.Where(row => row.UserId == id);
        }
        if (endpoint is not null)
        {
            query = query.Where(row => row.Endpoint == endpoint);
        }
        return await query.OrderBy(row => row.Id)
            .Select(row => new PushTestTarget(row.Id, row.Endpoint, row.DeviceLabel))
            .ToListAsync(cancellationToken);
    }

    // Sequential on purpose: two devices ringing at once must not race on the same SQLite writer,
    // and the second device's outcome must not depend on the first device's latency.
    internal async Task<IReadOnlyList<PushSendResult>> SendAsync(IReadOnlyList<PushTestTarget> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var keys = _keys.Keys ?? throw new InvalidOperationException("push_unavailable");
        var results = new List<PushSendResult>(targets.Count);
        foreach (var target in targets)
        {
            results.Add(await _sender.SendAsync(target.Id, Message, keys, reminderLogId: null,
                cancellationToken));
        }
        return results;
    }
}

// A device a test send will be attempted on. The endpoint is carried for logging (host only) and is
// never returned to the client: it is a bearer-like secret.
internal sealed record PushTestTarget(int Id, string Endpoint, string? DeviceLabel);
