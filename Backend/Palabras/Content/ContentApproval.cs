using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content;

internal sealed record ContentApprovalResult(int Approved, int Unchanged);

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
        foreach (var word in words)
        {
            if (word.Status == "approved")
            {
                unchanged++;
                continue;
            }
            // Import revisions retain the complete source shape, including metadata not stored on Word.
            // Approval changes status, not content/version/hash; retain that source shape for M4 auditing.
            var source = await database.WordRevisions.AsNoTracking()
                .Where(revision => revision.WordId == word.Id && revision.Version == word.Version)
                .OrderByDescending(revision => revision.Id).Select(revision => revision.SnapshotJson)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Missing content snapshot for word: " + word.Key + ". No words approved.");
            var snapshot = JsonNode.Parse(source)?.AsObject()
                ?? throw new InvalidOperationException("Invalid content snapshot for word: " + word.Key);
            snapshot["status"] = "approved";
            snapshot["updatedAt"] = now.ToString("O");
            word.Status = "approved";
            word.UpdatedAt = now.ToUnixTimeMilliseconds();
            database.WordRevisions.Add(new WordRevision
            {
                WordId = word.Id, Version = word.Version, Action = "approve",
                // Filesystem operator has no authenticated user identity, matching import revisions.
                ActorUserId = null, SnapshotJson = snapshot.ToJsonString(), CreatedAt = word.UpdatedAt
            });
            approved++;
            changedWordIds.Add(word.Id);
        }
        await database.SaveChangesAsync(cancellationToken);
        // --all-pending also repairs the pre-SRS approved corpus, even with no pending words.
        var wordIds = allPending
            ? await database.Words.Where(word => word.Status == "approved").Select(word => word.Id).ToArrayAsync(cancellationToken)
            : words.Select(word => word.Id).ToArray();
        await ContentCardLifecycle.ApproveAsync(database, wordIds, changedWordIds, now.ToUnixTimeMilliseconds(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(approved, unchanged);
    }
}
