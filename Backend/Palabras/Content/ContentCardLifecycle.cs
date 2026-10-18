using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Auth;
using Palabras.Persistence;

namespace Palabras.Content;

// Caller owns the transaction: content status, revisions and card eligibility commit together.
internal static class ContentCardLifecycle
{
    internal static async Task ApproveAsync(PalabrasDbContext database, int[] wordIds,
        IReadOnlySet<int> changedWordIds, long nowMs, CancellationToken cancellationToken)
    {
        // Retained removed rows have no soft-delete column. The latest current-version source
        // snapshot is authoritative for active ordinals, including manually/leech-paused cards.
        var snapshots = await (from revision in database.WordRevisions.AsNoTracking()
                               join word in database.Words on revision.WordId equals word.Id
                               where wordIds.Contains(word.Id) && revision.Version == word.Version
                               orderby revision.Id descending
                               select new { revision.WordId, revision.SnapshotJson }).ToListAsync(cancellationToken);
        var senseCounts = snapshots.DistinctBy(revision => revision.WordId).ToDictionary(revision => revision.WordId,
            revision => JsonNode.Parse(revision.SnapshotJson)?["senses"]?.AsArray().Count
                ?? throw new InvalidOperationException("Missing senses in content snapshot for word id: " + revision.WordId));
        if (wordIds.Any(wordId => !senseCounts.ContainsKey(wordId)))
            throw new InvalidOperationException("Missing content snapshot for card approval.");
        var senses = await database.Senses.Where(sense => wordIds.Contains(sense.WordId))
            .ToListAsync(cancellationToken);
        senses = senses.Where(sense => sense.SenseOrder < senseCounts[sense.WordId]).ToList();
        var senseIds = senses.Select(sense => sense.Id).ToArray();
        var cards = await database.Cards.Where(card => senseIds.Contains(card.SenseId)).ToListAsync(cancellationToken);
        var changedSenseIds = senses.Where(sense => changedWordIds.Contains(sense.WordId))
            .Select(sense => sense.Id).ToHashSet();
        foreach (var card in cards)
        {
            var clearReason = card.SuspendedReason == "word_not_approved";
            if (clearReason || changedSenseIds.Contains(card.SenseId))
            {
                card.Epoch = checked(card.Epoch + 1);
            }
            if (clearReason) card.SuspendedReason = null;
        }

        // Use the cookie-auth role mapping rather than inventing a second owner/learner rule.
        var users = await database.Users.AsNoTracking().ToListAsync(cancellationToken);
        var learners = users.Where(user => AccountAuthentication.Roles(user).Contains("learner")).ToArray();
        var existing = cards.Select(card => (card.SenseId, card.UserId, card.Direction)).ToHashSet();
        foreach (var sense in senses)
        foreach (var user in learners)
        foreach (var direction in new[] { "es_en", "en_es" })
        {
            if (!existing.Add((sense.Id, user.Id, direction))) continue;
            database.Cards.Add(new Card
            {
                SenseId = sense.Id, UserId = user.Id, Direction = direction,
                DueMs = nowMs, CreatedAt = nowMs
            });
        }
    }

    internal static async Task ReconcileSensesAsync(PalabrasDbContext database, IReadOnlyList<Sense> senses,
        int activeSenseCount, CancellationToken cancellationToken)
    {
        var senseIds = senses.Select(sense => sense.Id).ToArray();
        var cards = await database.Cards.Where(card => senseIds.Contains(card.SenseId)).ToListAsync(cancellationToken);
        foreach (var sense in senses)
        {
            var senseCards = cards.Where(card => card.SenseId == sense.Id).ToArray();
            if (sense.SenseOrder >= activeSenseCount)
            {
                // Keep both FK targets whenever cards exist, even without review history yet.
                if (senseCards.Length == 0) database.Senses.Remove(sense);
                foreach (var card in senseCards)
                {
                    if (card.SuspendedReason is not ("manual" or "leech"))
                        card.SuspendedReason = "sense_removed";
                }
            }
            else
            {
                foreach (var card in senseCards.Where(card => card.SuspendedReason == "sense_removed"))
                    card.SuspendedReason = "word_not_approved";
            }
        }
        // DemoteAsync already bumped all these epochs once for this changed-content import.
        // Restored senses still belong to a pending word; only reapproval makes them eligible.
    }

    internal static async Task DemoteAsync(PalabrasDbContext database, int wordId, CancellationToken cancellationToken)
    {
        var cards = await database.Cards.Where(card => database.Senses.Any(sense =>
            sense.Id == card.SenseId && sense.WordId == wordId)).ToListAsync(cancellationToken);
        foreach (var card in cards)
        {
            // Content edits invalidate old answers even if the card was already manually paused.
            card.Epoch = checked(card.Epoch + 1);
            card.SuspendedReason ??= "word_not_approved";
        }
    }
}
