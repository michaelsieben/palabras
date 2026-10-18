using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Backup;

internal static class RestoreProgressCli
{
    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length != 2 || args[0] != "restore-progress")
                throw new InvalidDataException("Usage: restore-progress <file.json>");
            // Parse and validate the complete file BEFORE opening the target. Never migrate or create a target.
            var export = ProgressFiles.Parse(await File.ReadAllTextAsync(args[1], cancellationToken));
            if (!File.Exists(settings.FilePath)) throw new InvalidDataException("Target database does not exist.");
            await using var database = settings.CreateContext();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (!await database.Users.AnyAsync(user => user.Id == export.UserId, cancellationToken) ||
                await ProgressFiles.FingerprintAsync(database, export.UserId, cancellationToken) != export.TargetFingerprint)
                throw new InvalidDataException("Target database identity does not match export.");

            var senseWords = await database.Senses.ToDictionaryAsync(sense => sense.Id, sense => sense.WordId, cancellationToken);
            var existingCards = await database.Cards.ToDictionaryAsync(card => card.Id, cancellationToken);
            var existingReviews = await database.ReviewLogs.ToDictionaryAsync(review => review.Id, cancellationToken);
            var existingAttempts = existingReviews.Values.ToDictionary(review => review.ClientAttemptId, StringComparer.Ordinal);
            foreach (var card in export.Cards)
            {
                if (!senseWords.ContainsKey(card.SenseId) ||
                    existingCards.TryGetValue(card.Id, out var current) &&
                    (current.UserId != export.UserId || current.SenseId != card.SenseId || current.Direction != card.Direction) ||
                    existingCards.Values.Any(value => value.Id != card.Id && value.UserId == export.UserId &&
                        value.SenseId == card.SenseId && value.Direction == card.Direction))
                    throw new InvalidDataException("Card identity conflicts with target database.");
            }
            var exportedCards = export.Cards.ToDictionary(card => card.Id);
            foreach (var review in export.ReviewLog)
            {
                if (senseWords[exportedCards[review.CardId].SenseId] != review.WordId ||
                    existingReviews.TryGetValue(review.Id, out var current) && !Same(current, review) ||
                    existingAttempts.TryGetValue(review.ClientAttemptId, out var attempt) && !Same(attempt, review))
                    throw new InvalidDataException("Review identity conflicts with target database.");
            }
            // All target checks complete. One atomic merge; credentials, content and other users stay unchanged.
            foreach (var card in export.Cards)
            {
                if (existingCards.TryGetValue(card.Id, out var current)) database.Entry(current).CurrentValues.SetValues(card);
                else database.Cards.Add(card);
            }
            await database.SaveChangesAsync(cancellationToken);
            foreach (var review in export.ReviewLog)
                if (!existingReviews.ContainsKey(review.Id)) database.ReviewLogs.Add(review);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await output.WriteLineAsync($"Restored {export.Cards.Length} cards and {export.ReviewLog.Length} reviews for user {export.UserId}.");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or InvalidOperationException or
            ArgumentException or SqliteException or DbUpdateException)
        {
            await error.WriteLineAsync($"Restore refused: {exception.Message}");
            return 1;
        }
    }

    private static bool Same(ReviewLog left, ReviewLog right) =>
        JsonSerializer.Serialize(left, ProgressFiles.Json) == JsonSerializer.Serialize(right, ProgressFiles.Json);
}
