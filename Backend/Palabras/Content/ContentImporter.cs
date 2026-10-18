using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content;

internal sealed record ContentImportResult(int Created, int Updated, int Unchanged, IReadOnlyList<string> Rejected);

internal sealed class ContentImporter
{
    private readonly DatabaseSettings _settings;

    internal ContentImporter(DatabaseSettings settings)
    {
        _settings = settings;
    }

    internal async Task<ContentImportResult> ImportAsync(string directory, bool dryRun, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException("Import directory does not exist: " + directory);
        }
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "content", "schema", "word.schema.json"));
        var entries = new List<Entry>();
        var rejected = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // Materialize and validate the entire batch before initialization, migration, or any write.
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                using var document = JsonDocument.Parse(json);
                RejectDuplicateProperties(document.RootElement);
                var root = JsonNode.Parse(json)!;
                var evaluation = schema.Evaluate(root, new EvaluationOptions
                {
                    RequireFormatValidation = true,
                    OutputFormat = OutputFormat.List
                });
                if (!evaluation.IsValid)
                {
                    var errors = evaluation.Details.Where(detail => detail.Errors is { Count: > 0 })
                        .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Key}: {error.Value}"));
                    rejected.Add(Path.GetFileName(path) + ": JSON schema validation failed: " + string.Join("; ", errors));
                    continue;
                }
                var key = root["id"]!.GetValue<string>();
                if (!StringComparer.Ordinal.Equals(Path.GetFileName(path), key + ".json"))
                {
                    rejected.Add(Path.GetFileName(path) + ": filename must equal " + key + ".json");
                    continue;
                }
                if (!keys.Add(key))
                {
                    rejected.Add(Path.GetFileName(path) + ": duplicate id " + key);
                    continue;
                }
                if (!DateTimeOffset.TryParse(Text(root, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                {
                    rejected.Add(Path.GetFileName(path) + ": createdAt cannot be represented as a database timestamp");
                    continue;
                }
                entries.Add(new Entry(root, key, Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(root))), createdAt.ToUnixTimeMilliseconds()));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or IOException or UnauthorizedAccessException)
            {
                rejected.Add(Path.GetFileName(path) + ": " + exception.Message);
            }
        }
        if (rejected.Count > 0)
        {
            return new(0, 0, 0, rejected);
        }
        if (entries.Count == 0)
        {
            return new(0, 0, 0, []);
        }
        if (dryRun && !File.Exists(_settings.FilePath))
        {
            return new(entries.Count, 0, 0, []);
        }
        if (!dryRun)
        {
            await DatabaseLifecycle.InitializeAsync(_settings, cancellationToken);
        }
        await using var database = _settings.CreateContext();
        if (dryRun)
        {
            // Read-only connection ensures --dry-run cannot create a DB or change existing content/schema.
            var connection = database.Database.GetDbConnection();
            connection.ConnectionString = new SqliteConnectionStringBuilder(connection.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();
            var hasWords = await database.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='words'")
                .SingleAsync(cancellationToken);
            if (hasWords == 0)
            {
                return new(entries.Count, 0, 0, []);
            }
            var hashes = await database.Words.AsNoTracking().ToDictionaryAsync(word => word.Key, word => word.ContentHash, cancellationToken);
            return new(entries.Count(entry => !hashes.ContainsKey(entry.Key)),
                entries.Count(entry => hashes.TryGetValue(entry.Key, out var hash) && hash != entry.Hash),
                entries.Count(entry => hashes.TryGetValue(entry.Key, out var hash) && hash == entry.Hash), []);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var entry in entries)
        {
            var word = await database.Words.SingleOrDefaultAsync(word => word.Key == entry.Key, cancellationToken);
            if (word?.ContentHash == entry.Hash)
            {
                unchanged++;
                continue;
            }
            var isNew = word == null;
            word ??= new Word { Key = entry.Key };
            var root = entry.Root;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (isNew)
            {
                word.CreatedAt = entry.CreatedAt;
                word.Status = Text(root, "status") == "draft" ? "draft" : "pending_review";
                database.Words.Add(word);
                created++;
            }
            else
            {
                word.Version++;
                word.Status = "pending_review";
                updated++;
            }
            word.Headword = Text(root, "headword")!;
            word.Article = Text(root, "article");
            word.Gender = Text(root, "gender");
            word.Pos = Text(root, "partOfSpeech")!;
            word.Cefr = Text(root, "cefr")!;
            word.RegionScope = Text(root["region"]!, "scope")!;
            word.RegionNotes = Text(root["region"]!, "notes");
            word.Register = Text(root, "register")!;
            word.RegisterNotes = Text(root, "registerNotes");
            word.Notes = Text(root, "notes");
            word.Plural = Text(root, "plural");
            word.ProvenanceMethod = Text(root["provenance"]!, "method")!;
            word.ProvenanceRun = Text(root["provenance"]!, "run");
            word.ContentHash = entry.Hash;
            word.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            await PopulateChildrenAsync(database, word, root, cancellationToken);
            var snapshot = root.DeepClone();
            snapshot["status"] = word.Status;
            database.WordRevisions.Add(new WordRevision
            {
                WordId = word.Id, Version = word.Version, Action = "import",
                SnapshotJson = snapshot.ToJsonString(), CreatedAt = now
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(created, updated, unchanged, []);
    }

    private static async Task PopulateChildrenAsync(PalabrasDbContext database, Word word, JsonNode root, CancellationToken cancellationToken)
    {
        // Preserve IDs for retained sense ordinals: later cards reference these IDs.
        var senses = await database.Senses.Where(sense => sense.WordId == word.Id).ToListAsync(cancellationToken);
        var sourceSenses = root["senses"]!.AsArray();
        for (var index = 0; index < sourceSenses.Count; index++)
        {
            var source = sourceSenses[index]!;
            var sense = senses.SingleOrDefault(sense => sense.SenseOrder == index);
            if (sense == null)
            {
                sense = new Sense { WordId = word.Id, SenseOrder = index };
                database.Senses.Add(sense);
            }
            sense.EnGloss = Text(source, "gloss")!;
            sense.EnAccepted = source["acceptedEn"]?.ToJsonString() ?? JsonSerializer.Serialize(sense.EnGloss.Split(", ", StringSplitOptions.None));
            sense.EsAccepted = source["acceptedEs"]?.ToJsonString() ?? JsonSerializer.Serialize(new[] { word.Headword });
            sense.ExampleEs = Text(source, "example")!;
            sense.ExampleEn = Text(source, "exampleGloss")!;
            sense.AccentsAreThePoint = source["accentsAreThePoint"]?.GetValue<bool>() ?? false;
            sense.Notes = Text(source, "notes");
        }
        database.Senses.RemoveRange(senses.Where(sense => sense.SenseOrder >= sourceSenses.Count));
        database.WordAlternatives.RemoveRange(await database.WordAlternatives.Where(alternative => alternative.WordId == word.Id).ToListAsync(cancellationToken));
        foreach (var (source, index) in root["alternatives"]!.AsArray().Select((source, index) => (source!, index)))
        {
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = word.Id, Rank = index, Kind = Text(source, "kind")!, Value = Text(source, "value")!,
                Gloss = Text(source, "gloss"), Reason = Text(source, "reason")!,
                TargetKey = Text(source, "targetId"), Example = Text(source, "example")
            });
        }
        var topics = root["topics"]!.AsArray().Select(topic => topic!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var existing = await database.WordTopics.Where(topic => topic.WordId == word.Id).ToListAsync(cancellationToken);
        database.WordTopics.RemoveRange(existing.Where(topic => !topics.Contains(topic.Topic)));
        foreach (var topic in topics.Except(existing.Select(topic => topic.Topic), StringComparer.Ordinal))
        {
            database.WordTopics.Add(new WordTopic { WordId = word.Id, Topic = topic });
        }
    }

    private static string? Text(JsonNode node, string property) => node[property]?.GetValue<string>();

    private static byte[] CanonicalBytes(JsonNode root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, root, true);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node, bool isRoot = false)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                if (isRoot && property.Key is "status" or "review" or "updatedAt")
                {
                    continue;
                }
                writer.WritePropertyName(property.Key);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
            {
                WriteCanonical(writer, item);
            }
            writer.WriteEndArray();
        }
        else if (node == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            // Decode strings before writing so literal UTF-8 and equivalent JSON escapes hash identically.
            if (node.GetValueKind() == JsonValueKind.String)
            {
                writer.WriteStringValue(node.GetValue<string>());
            }
            else
            {
                node.WriteTo(writer);
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Duplicate JSON property: " + property.Name);
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private sealed record Entry(JsonNode Root, string Key, string Hash, long CreatedAt);
}
