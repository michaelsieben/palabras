using System.Globalization;
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
                entries.Add(new Entry(root, key, CanonicalJson.Sha256Hex(CanonicalJson.ContentBytes(root)), createdAt.ToUnixTimeMilliseconds()));
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

        // Serialize the hash check with reviewer writes: whichever commits first determines the
        // version the next operation reads. Never upgrade a stale read transaction to a writer.
        await database.Database.OpenConnectionAsync(cancellationToken);
        var writableConnection = (SqliteConnection)database.Database.GetDbConnection();
        await using var transaction = writableConnection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        await database.Database.UseTransactionAsync(transaction, cancellationToken);
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        // Trust-by-default: an imported word is approved on arrival; a reviewer objects later.
        // The ids are re-approved below so the card pass creates/refreshes learner cards and the
        // changed-content epoch bump stays inside this same transaction.
        var approvedWordIds = new List<int>();
        var changedApprovedWordIds = new HashSet<int>();
        foreach (var entry in entries)
        {
            var word = await database.Words.SingleOrDefaultAsync(word => word.Key == entry.Key, cancellationToken);
            if (word?.ContentHash == entry.Hash)
            {
                // Hash tracks the last source import, not the reviewer-edited snapshot. An
                // unchanged source must keep her edit, verdict, version and card epochs intact.
                unchanged++;
                continue;
            }
            var isNew = word == null;
            word ??= new Word { Key = entry.Key };
            var root = entry.Root;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var contentChanged = false;
            if (isNew)
            {
                word.CreatedAt = entry.CreatedAt;
                // `draft` is the one explicit hold the source can request; every other file status
                // is stale metadata under trust-by-default, so the word arrives approved.
                word.Status = Text(root, "status") == "draft" ? "draft" : "approved";
                if (word.Status == "approved")
                {
                    word.ApprovedVersion = word.Version;
                }
                database.Words.Add(word);
                created++;
            }
            else if (word.Status == "approved")
            {
                // Q-M4-3 changed-source behaviour, amended for trust-by-default: bump and accept the
                // new content, but stay approved. Demoting here would silently empty the learner's
                // queue for a word a reviewer had already accepted; the reviewer can object instead.
                word.Version = checked(word.Version + 1);
                word.ApprovedVersion = word.Version;
                word.Status = "approved";
                contentChanged = true;
                updated++;
            }
            else
            {
                // Any other current status (pending_review, rejected, needs_change, draft) keeps the
                // shipped re-review behaviour: the changed source returns it to the reviewer.
                await ContentCardLifecycle.DemoteAsync(database, word.Id, cancellationToken);
                word.Version = checked(word.Version + 1);
                word.Status = "pending_review";
                updated++;
            }
            word.Headword = Text(root, "headword")!;
            word.Article = Text(root, "article");
            word.Gender = Text(root, "gender");
            word.Pos = Text(root, "partOfSpeech")!;
            word.Cefr = Text(root, "cefr")!;
            // Intra-band authoring rank (content schema `difficulty`). A file without it maps to
            // NULL: the new-card ordering coalesces NULL to int.MaxValue, above the schema's 9999
            // ceiling, so unranked words sort after every ranked word.
            word.Difficulty = root["difficulty"]?.GetValue<int>();
            word.RegionScope = Text(root["region"]!, "scope")!;
            word.RegionNotes = Text(root["region"]!, "notes");
            word.Register = Text(root, "register")!;
            word.RegisterNotes = Text(root, "registerNotes");
            word.Notes = Text(root, "notes");
            word.Plural = Text(root, "plural");
            word.ProvenanceMethod = Text(root["provenance"]!, "method")!;
            word.ProvenanceRun = Text(root["provenance"]!, "run");
            word.ContentHash = entry.Hash;
            word.StatusChangedAt = now;
            // ApprovedVersion remains the last approved version (audit only). Revisions and
            // word_reviews are append-only: changed source never rewrites an old verdict.
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
            if (word.Status == "approved")
            {
                // The import revision above already records the approval; this list only drives the
                // card pass, which needs the database id.
                approvedWordIds.Add(word.Id);
                if (contentChanged)
                {
                    changedApprovedWordIds.Add(word.Id);
                }
            }
        }
        if (approvedWordIds.Count > 0)
        {
            await ContentCardLifecycle.ApproveAsync(database, approvedWordIds.ToArray(), changedApprovedWordIds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken);
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
        await ContentCardLifecycle.ReconcileSensesAsync(database, senses, sourceSenses.Count, cancellationToken);
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
