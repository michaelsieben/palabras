using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content;

internal static class ContentRetirement
{
    internal static async Task<int> RetireAsync(PalabrasDbContext database, IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var words = await database.Words.Where(word => keys.Contains(word.Key)).ToListAsync(cancellationToken);
        var unknown = keys.Except(words.Select(word => word.Key), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("Unknown word key(s): " + string.Join(", ", unknown) + ". No words retired.");
        var retired = 0;
        var now = TimeProvider.System.GetUtcNow();
        foreach (var word in words.Where(word => word.Status != "retired"))
        {
            var source = await database.WordRevisions.AsNoTracking()
                .Where(revision => revision.WordId == word.Id && revision.Version == word.Version)
                .OrderByDescending(revision => revision.Id).Select(revision => revision.SnapshotJson)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Missing content snapshot for word: " + word.Key + ". No words retired.");
            var snapshot = JsonNode.Parse(source)?.AsObject()
                ?? throw new InvalidOperationException("Invalid content snapshot for word: " + word.Key);
            snapshot["status"] = "retired";
            snapshot["updatedAt"] = now.ToString("O");
            word.Status = "retired";
            word.UpdatedAt = now.ToUnixTimeMilliseconds();
            await ContentCardLifecycle.DemoteAsync(database, word.Id, cancellationToken);
            database.WordRevisions.Add(new WordRevision
            {
                WordId = word.Id, Version = word.Version, Action = "retire",
                SnapshotJson = snapshot.ToJsonString(), CreatedAt = word.UpdatedAt
            });
            retired++;
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return retired;
    }
}
