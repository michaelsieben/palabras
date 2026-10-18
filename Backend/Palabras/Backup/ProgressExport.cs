using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Backup;

internal sealed record ProgressExport(int Version, int UserId, string TargetFingerprint,
    DateTimeOffset ExportedAtUtc, Card[] Cards, ReviewLog[] ReviewLog);

internal static class ProgressFiles
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    internal static async Task<string> FingerprintAsync(PalabrasDbContext database, int userId, CancellationToken cancellationToken)
    {
        var user = await database.Users.AsNoTracking().SingleAsync(value => value.Id == userId, cancellationToken);
        // No password hashes or account names in exports. Require the same account lineage and content IDs.
        var words = await database.Words.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.Key, value.ContentHash }).ToArrayAsync(cancellationToken);
        var senses = await database.Senses.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.WordId, value.SenseOrder, value.EnGloss, value.ExampleEs, value.ExampleEn })
            .ToArrayAsync(cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { user.Id, user.CreatedAt, words, senses }, Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static async Task<ProgressExport> ExportAsync(DatabaseSettings settings, int userId, TimeProvider time,
        CancellationToken cancellationToken)
    {
        await using var database = settings.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var result = new ProgressExport(1, userId, await FingerprintAsync(database, userId, cancellationToken), time.GetUtcNow(),
            await database.Cards.AsNoTracking().Where(value => value.UserId == userId).OrderBy(value => value.Id).ToArrayAsync(cancellationToken),
            await database.ReviewLogs.AsNoTracking().Where(value => value.UserId == userId).OrderBy(value => value.Id).ToArrayAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static byte[] Csv(ProgressExport export)
    {
        // One rectangular CSV contains both tables, with all fields in the union of their columns.
        var cards = JsonSerializer.SerializeToElement(export.Cards, Json);
        var reviews = JsonSerializer.SerializeToElement(export.ReviewLog, Json);
        var columns = typeof(Card).GetProperties().Concat(typeof(ReviewLog).GetProperties())
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Distinct().ToArray();
        var text = new StringBuilder("table," + string.Join(',', columns) + "\r\n");
        foreach (var (table, rows) in new[] { ("cards", cards), ("review_log", reviews) })
        foreach (var row in rows.EnumerateArray())
        {
            text.Append(table);
            foreach (var column in columns)
            {
                var value = row.TryGetProperty(column, out var field) && field.ValueKind != JsonValueKind.Null
                    ? field.ValueKind == JsonValueKind.String ? field.GetString()! : field.GetRawText() : "";
                // Neutralize spreadsheet formula execution for user-provided answer text.
                if (field.ValueKind == JsonValueKind.String && value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
                text.Append(",\"").Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            text.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static ProgressExport Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        RequireProperties(document.RootElement, typeof(ProgressExport));
        foreach (var card in document.RootElement.GetProperty("cards").EnumerateArray()) RequireProperties(card, typeof(Card));
        foreach (var review in document.RootElement.GetProperty("reviewLog").EnumerateArray()) RequireProperties(review, typeof(ReviewLog));
        var export = JsonSerializer.Deserialize<ProgressExport>(text, Json) ?? throw new InvalidDataException("Missing export.");
        if (export.Version != 1 || export.UserId <= 0 || export.TargetFingerprint is null ||
            export.TargetFingerprint.Length != 64 || export.Cards is null || export.ReviewLog is null)
            throw new InvalidDataException("Invalid export metadata.");
        var ids = new HashSet<int>();
        var identities = new HashSet<(int, string)>();
        foreach (var card in export.Cards)
        {
            if (card.Id <= 0 || !ids.Add(card.Id) || card.UserId != export.UserId || card.SenseId <= 0 ||
                !identities.Add((card.SenseId, card.Direction)) || card.Direction is not ("es_en" or "en_es") ||
                !Enum.IsDefined(card.State) || card.Epoch < 0 || card.Step < 0 || card.Reps < 0 || card.Lapses < 0 ||
                !Positive(card.Stability) || !Difficulty(card.Difficulty) ||
                card.SuspendedReason is not (null or "word_not_approved" or "sense_removed" or "manual" or "leech") ||
                !LocalDate(card.BuriedUntilLocalDate) || !LocalDate(card.IntroducedLocalDate) ||
                !Instant(card.DueMs) || !Instant(card.LastReviewMs) || !Instant(card.CreatedAt))
                throw new InvalidDataException("Invalid card.");
        }
        var reviewIds = new HashSet<int>();
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var review in export.ReviewLog)
        {
            if (review.Id <= 0 || !reviewIds.Add(review.Id) || review.UserId != export.UserId || !ids.Contains(review.CardId) ||
                review.WordId <= 0 || review.CardEpoch < 0 || !Guid.TryParse(review.ClientAttemptId, out _) ||
                !attempts.Add(review.ClientAttemptId) || !Enum.IsDefined(review.Rating) || !Enum.IsDefined(review.StateBefore) ||
                !Enum.IsDefined(review.StateAfter) || review.Mode is not ("recognition" or "typed" or "cloze") ||
                review.DurationMs is < 0 or > 600000 || review.WeightsVersion < 0 ||
                !Nonnegative(review.ElapsedDays) || !Nonnegative(review.IntervalBeforeDays) || !Nonnegative(review.IntervalAfterDays) ||
                !Positive(review.StabilityBefore) || !Positive(review.StabilityAfter) ||
                !Difficulty(review.DifficultyBefore) || !Difficulty(review.DifficultyAfter) ||
                !Instant(review.ReviewedAtMs) || !Instant(review.ReceivedAtMs) || !Instant(review.DueAfterMs))
                throw new InvalidDataException("Invalid review.");
        }
        return export;
    }

    private static void RequireProperties(JsonElement element, Type type)
    {
        var expected = type.GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).ToHashSet();
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != expected.Count ||
            !expected.SetEquals(element.EnumerateObject().Select(property => property.Name)))
            throw new InvalidDataException("Missing, duplicate or unknown export fields.");
    }

    private static bool Positive(double? value) => value is null || double.IsFinite(value.Value) && value > 0;
    private static bool Difficulty(double? value) => value is null || double.IsFinite(value.Value) && value is >= 1 and <= 10;
    private static bool Nonnegative(double? value) => value is null || double.IsFinite(value.Value) && value >= 0;
    private static bool Instant(long? value) => value is null || value is >= -62135596800000 and <= 253402300799999;
    private static bool LocalDate(string? value) => value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
