using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Palabras.Persistence;

namespace Palabras.Push;

// Plan §2.2 asks for a WARN when a generated key meets existing subscriptions. The file is the
// deployment's identity: if it disappears while the database survives (lost volume, restore
// without the file, deploy guard not yet copying it) a new key is minted and every stored
// subscription is silently bound to a key the push services will refuse. The frozen schema already
// records the binding per row (`push_subscriptions.vapid_key_id`), so the count is exact rather
// than derived, and no column or migration is needed.
internal sealed class VapidKeyAudit
{
    private VapidKeyAudit()
    {
    }

    // Returns the number of rows bound to another key (0 when no key was generated or nothing is
    // stored). A database failure is logged and contained: push being partly unusable must never
    // stop the app from serving.
    internal static async Task<int> WarnIfOrphanedAsync(VapidKeyStore store, DatabaseSettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (!store.GeneratedOnLoad || store.Keys is not { } keys)
        {
            return 0;
        }
        int orphaned;
        try
        {
            await using var database = settings.CreateContext();
            orphaned = await CountBoundToOtherKeyAsync(database, keys.KeyId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Generated a new VAPID key, but existing push subscriptions could not be checked ({Reason}). " +
                "Verify that every device re-subscribes.", exception.Message);
            return 0;
        }
        if (orphaned == 0)
        {
            return 0;
        }
        logger.LogWarning(
            "VAPID key generated; {Count} existing subscriptions are bound to another key and will fail " +
            "until each device subscribes again (current key_id {KeyId}). Restore the previous key file " +
            "from backup to keep them working.",
            orphaned, keys.KeyId);
        return orphaned;
    }

    // Fingerprints are stored per row, so "bound to another key" is a plain comparison against the
    // current key. Rows in every state are counted: the operator is being told what the new key
    // invalidated, not how many devices are currently deliverable.
    internal static Task<int> CountBoundToOtherKeyAsync(PalabrasDbContext database, string keyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        return database.PushSubscriptions.AsNoTracking()
            .CountAsync(row => row.VapidKeyId != keyId, cancellationToken);
    }
}
