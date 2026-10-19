using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Review;

namespace Palabras.Content;

internal sealed record ContentApprovalResult(int Approved, int Unchanged);

// CLI approval: host/operator path, no authenticated reviewer, so word_reviews is not written
// (reviewer_id is NOT NULL). The state machine itself is the shared ReviewTransitions core; the CLI
// keeps its shipped lenient "approve from any status" semantics and owns the aggregate card pass.
internal static class ContentApproval
{
    internal static async Task<ContentApprovalResult> ApproveAsync(PalabrasDbContext database,
        IReadOnlyList<string> keys, bool allPending, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var words = await database.Words.Where(word => allPending ? word.Status == "pending_review" : keys.Contains(word.Key))
            .OrderBy(word => word.Key).ToListAsync(cancellationToken);
        var unknown = keys.Except(words.Select(word => word.Key), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException("Unknown word key(s): " + string.Join(", ", unknown) + ". No words approved.");
        }
        var approved = 0;
        var unchanged = 0;
        var changedWordIds = new HashSet<int>();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var word in words)
        {
            if (word.Status == "approved")
            {
                unchanged++;
                continue;
            }
            var outcome = await ReviewTransitions.ApplyAsync(database, word,
                new ReviewTransitionRequest(word.Version, ReviewActionNames.Approve, null, null, null, null),
                ReviewTransitions.AnyStatus, actorUserId: null, nowMs, applyCardEffects: false, cancellationToken);
            if (outcome.Result != ReviewResults.Applied)
            {
                throw new InvalidOperationException("Word cannot be approved: " + word.Key + ". No words approved.");
            }
            approved++;
            changedWordIds.Add(word.Id);
        }
        await database.SaveChangesAsync(cancellationToken);
        // --all-pending also repairs the pre-SRS approved corpus, even with no pending words.
        var wordIds = allPending
            ? await database.Words.Where(word => word.Status == "approved").Select(word => word.Id).ToArrayAsync(cancellationToken)
            : words.Select(word => word.Id).ToArray();
        await ContentCardLifecycle.ApproveAsync(database, wordIds, changedWordIds, nowMs, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(approved, unchanged);
    }
}
