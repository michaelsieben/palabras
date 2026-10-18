using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Learn;

// S6b retains a sense row that the content file no longer lists, so `review_log.card_id` and the
// card→sense FK can never break; only a removed sense with no cards is deleted. There is no
// soft-delete column, so the current version's content snapshot is the only record of which
// ordinals are still in the corpus (the same rule `ContentCardLifecycle.ApproveAsync` uses:
// sense_order < snapshot sense count). A card on a retained removed sense keeps whatever
// `suspended_reason` it had — manual/leech survive — so suspension alone cannot identify removal.
// Every learner read path therefore gates on this set too.
//
// Cost: one query for the current-version revisions of approved words plus one for their senses.
// The derived sense ids are passed to EF as an IN list, which stays far below SQLite's parameter
// limit for a corpus of this size (58 words / 62 senses today).
internal sealed class ActiveSenses
{
    private static readonly IReadOnlyDictionary<int, int> NoCounts = new Dictionary<int, int>();

    internal static ActiveSenses Empty { get; } = new(new HashSet<int>(), NoCounts);

    private ActiveSenses(IReadOnlySet<int> senseIds, IReadOnlyDictionary<int, int> countsByWordId)
    {
        SenseIds = senseIds;
        CountsByWordId = countsByWordId;
    }

    internal IReadOnlySet<int> SenseIds { get; }

    internal IReadOnlyDictionary<int, int> CountsByWordId { get; }

    internal bool IsActive(int senseId) => SenseIds.Contains(senseId);

    // Active ordinal count for one word; 0 means the word contributes nothing to learner reads.
    internal int CountFor(int wordId) => CountsByWordId.TryGetValue(wordId, out var count) ? count : 0;

    internal static async Task<ActiveSenses> LoadAsync(PalabrasDbContext database,
        IReadOnlyCollection<int>? wordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var counts = await CountsAsync(database, wordIds, cancellationToken);
        if (counts.Count == 0)
        {
            return Empty;
        }
        var ids = counts.Keys.ToArray();
        var senses = await database.Senses.AsNoTracking()
            .Where(sense => ids.Contains(sense.WordId))
            .Select(sense => new { sense.Id, sense.WordId, sense.SenseOrder })
            .ToListAsync(cancellationToken);
        var active = senses
            .Where(sense => sense.SenseOrder >= 0 && sense.SenseOrder < counts[sense.WordId])
            .Select(sense => sense.Id)
            .ToHashSet();
        return new ActiveSenses(active, counts);
    }

    private static async Task<Dictionary<int, int>> CountsAsync(PalabrasDbContext database,
        IReadOnlyCollection<int>? wordIds, CancellationToken cancellationToken)
    {
        var filter = wordIds?.ToArray();
        if (filter is { Length: 0 })
        {
            return [];
        }
        // Push the word filter into the entity query, before the projection: no composing over a DTO.
        IQueryable<WordRevision> source = database.WordRevisions.AsNoTracking();
        if (filter is not null)
        {
            source = source.Where(revision => filter.Contains(revision.WordId));
        }
        // Latest revision of the current version wins, exactly like ContentCardLifecycle.ApproveAsync.
        var rows = await (from revision in source
                          join word in database.Words on revision.WordId equals word.Id
                          where word.Status == "approved" && revision.Version == word.Version
                          orderby revision.Id descending
                          select new { revision.WordId, revision.SnapshotJson }).ToListAsync(cancellationToken);
        var counts = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            if (counts.ContainsKey(row.WordId))
            {
                continue;
            }
            // An approved word with no current-version revision is invisible to learner reads
            // instead of failing every request: the write path (approval) still throws for that
            // inconsistent corpus state, S6b review observation 3.
            var senses = JsonNode.Parse(row.SnapshotJson)?["senses"]?.AsArray();
            if (senses is null)
            {
                continue;
            }
            counts[row.WordId] = senses.Count;
        }
        return counts;
    }
}
