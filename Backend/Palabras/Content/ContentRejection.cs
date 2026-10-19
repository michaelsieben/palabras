using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Review;

namespace Palabras.Content;

internal sealed record ContentRejectionResult(int Rejected, int Unchanged);

// Host/operator objection path. The reviewer API is the normal route; this exists so an operator
// can withdraw an already-approved word without impersonating a reviewer in the database.
// Like CLI approval, no word_reviews row is written (reviewer_id is NOT NULL); the transition's
// revision is the audit record.
internal static class ContentRejection
{
    // A draft is an authoring hold and a retired word is out of the corpus: neither is a reviewer
    // objection. Rejecting them would silently overwrite a deliberate status.
    private static readonly IReadOnlySet<string> RejectableFrom = new HashSet<string>(StringComparer.Ordinal)
    {
        "pending_review", "approved", "needs_change"
    };

    internal static async Task<ContentRejectionResult> RejectAsync(PalabrasDbContext database,
        IReadOnlyList<string> keys, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var words = await database.Words.Where(word => keys.Contains(word.Key))
            .OrderBy(word => word.Key).ToListAsync(cancellationToken);
        var unknown = keys.Except(words.Select(word => word.Key), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException("Unknown word key(s): " + string.Join(", ", unknown) + ". No words rejected.");
        }
        var nowMs = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var rejected = 0;
        var unchanged = 0;
        foreach (var word in words)
        {
            if (word.Status == "rejected")
            {
                unchanged++;
                continue;
            }
            if (!RejectableFrom.Contains(word.Status))
            {
                throw new InvalidOperationException(
                    "Word cannot be rejected from status '" + word.Status + "': " + word.Key + ". No words rejected.");
            }
            var outcome = await ReviewTransitions.ApplyAsync(database, word,
                new ReviewTransitionRequest(word.Version, ReviewActionNames.Reject, null, null, null, null),
                RejectableFrom, actorUserId: null, nowMs, applyCardEffects: true, cancellationToken);
            if (outcome.Result != ReviewResults.Applied)
            {
                throw new InvalidOperationException("Word cannot be rejected: " + word.Key + ". No words rejected.");
            }
            rejected++;
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(rejected, unchanged);
    }
}
