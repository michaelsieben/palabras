using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

public sealed class M2ContentImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "palabras-import-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;
    private readonly string _words;

    public M2ContentImportTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_directory, "data", "palabras.db"));
        _words = Path.Combine(_directory, "words");
        Directory.CreateDirectory(_words);
    }

    [Fact]
    public async Task CorpusImportsOneEntryPerFileThenReportsZeroChanges()
    {
        var files = CorpusFiles();
        var importer = new ContentImporter(_settings);
        var first = await importer.ImportAsync(CorpusDirectory(), false, CancellationToken.None);
        Assert.Equal(new[] { files.Length, 0, 0, 0 }, Counts(first));
        await using var database = _settings.CreateContext();
        Assert.Equal(files.Length, await database.Words.CountAsync());
        Assert.Equal(files.Length, await database.WordRevisions.CountAsync());
        var words = await database.Words.ToListAsync();
        Assert.Equal(files.Select(CorpusKey).Order(StringComparer.Ordinal),
            words.Select(word => word.Key).Order(StringComparer.Ordinal));
        var pluralByKey = files.ToDictionary(CorpusKey,
            path => JsonNode.Parse(File.ReadAllText(path))!["plural"]?.GetValue<string>());
        Assert.All(words, word =>
        {
            Assert.Equal("pending_review", word.Status);
            Assert.Equal(1, word.Version);
            Assert.Equal(64, word.ContentHash.Length);
            Assert.Equal(pluralByKey[word.Key], word.Plural);
        });
        var second = await importer.ImportAsync(CorpusDirectory(), false, CancellationToken.None);
        Assert.Equal(new[] { 0, 0, files.Length, 0 }, Counts(second));
        Assert.Equal(files.Length, await database.WordRevisions.CountAsync());
    }

    [Fact]
    public async Task MapsAllFieldsAcceptedAnswersAlternativesTopicsAndRevision()
    {
        var source = Seed();
        source["plural"] = "neveras";
        source["registerNotes"] = "Register note";
        source["region"]!["notes"] = "Region note";
        source["notes"] = "Word note";
        source["provenance"]!["run"] = "test-run";
        var sense = source["senses"]![0]!;
        sense["acceptedEn"] = new JsonArray("fridge", "refrigerator");
        sense["acceptedEs"] = new JsonArray("nevera", "la nevera");
        sense["accentsAreThePoint"] = true;
        sense["notes"] = "Sense note";
        await Write(source);
        Assert.Equal(new[] { 1, 0, 0, 0 }, Counts(await Import()));
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync();
        Assert.Equal(source["id"]!.GetValue<string>(), word.Key);
        Assert.Equal(source["headword"]!.GetValue<string>(), word.Headword);
        Assert.Equal(source["article"]!.GetValue<string>(), word.Article);
        Assert.Equal(source["gender"]!.GetValue<string>(), word.Gender);
        Assert.Equal("noun", word.Pos);
        Assert.Equal(source["cefr"]!.GetValue<string>(), word.Cefr);
        Assert.Equal(source["region"]!["scope"]!.GetValue<string>(), word.RegionScope);
        Assert.Equal(source["register"]!.GetValue<string>(), word.Register);
        Assert.Equal("Register note", word.RegisterNotes);
        Assert.Equal("Region note", word.RegionNotes);
        Assert.Equal("Word note", word.Notes);
        Assert.Equal("neveras", word.Plural);
        Assert.Equal("hand-authored", word.ProvenanceMethod);
        Assert.Equal("test-run", word.ProvenanceRun);
        Assert.Equal(DateTimeOffset.Parse(source["createdAt"]!.GetValue<string>()).ToUnixTimeMilliseconds(), word.CreatedAt);
        Assert.True(word.UpdatedAt > 0);
        var saved = await database.Senses.SingleAsync();
        Assert.Equal(0, saved.SenseOrder);
        Assert.Equal(sense["gloss"]!.GetValue<string>(), saved.EnGloss);
        Assert.Equal(sense["example"]!.GetValue<string>(), saved.ExampleEs);
        Assert.Equal(sense["exampleGloss"]!.GetValue<string>(), saved.ExampleEn);
        Assert.Equal(new[] { "fridge", "refrigerator" }, JsonSerializer.Deserialize<string[]>(saved.EnAccepted));
        Assert.Equal(new[] { "nevera", "la nevera" }, JsonSerializer.Deserialize<string[]>(saved.EsAccepted));
        Assert.True(saved.AccentsAreThePoint);
        Assert.Equal("Sense note", saved.Notes);
        var alternatives = await database.WordAlternatives.OrderBy(alternative => alternative.Rank).ToListAsync();
        Assert.Equal(source["alternatives"]!.AsArray().Count, alternatives.Count);
        for (var index = 0; index < alternatives.Count; index++)
        {
            var expected = source["alternatives"]![index]!;
            var actual = alternatives[index];
            Assert.Equal(index, actual.Rank);
            Assert.Equal(expected["kind"]!.GetValue<string>(), actual.Kind);
            Assert.Equal(expected["value"]!.GetValue<string>(), actual.Value);
            Assert.Equal(expected["reason"]!.GetValue<string>(), actual.Reason);
            Assert.Equal(expected["gloss"]?.GetValue<string>(), actual.Gloss);
            Assert.Equal(expected["targetId"]?.GetValue<string>(), actual.TargetKey);
            Assert.Equal(expected["example"]?.GetValue<string>(), actual.Example);
        }
        Assert.Equal(source["topics"]!.AsArray().Select(topic => topic!.GetValue<string>()).Order(),
            (await database.WordTopics.Select(topic => topic.Topic).ToListAsync()).Order());
        var revision = await database.WordRevisions.SingleAsync();
        Assert.Equal(word.Id, revision.WordId);
        Assert.Equal(1, revision.Version);
        Assert.Equal("import", revision.Action);
        Assert.Null(revision.ActorUserId);
        Assert.Equal("pending_review", JsonNode.Parse(revision.SnapshotJson)!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task DefaultsSplitEnglishGlossAndUseSpanishHeadword()
    {
        var source = Seed();
        source["senses"]![0]!["gloss"] = "fridge, refrigerator";
        await Write(source);
        await Import();
        await using var database = _settings.CreateContext();
        var sense = await database.Senses.SingleAsync();
        Assert.Equal(new[] { "fridge", "refrigerator" }, JsonSerializer.Deserialize<string[]>(sense.EnAccepted));
        Assert.Equal(new[] { source["headword"]!.GetValue<string>() }, JsonSerializer.Deserialize<string[]>(sense.EsAccepted));
        Assert.False(sense.AccentsAreThePoint);
    }

    [Fact]
    public async Task SemanticEditIncrementsVersionDemotesApprovalPreservesSenseIdAndWritesRevision()
    {
        var source = Seed();
        await Write(source);
        await Import();
        await using var database = _settings.CreateContext();
        var before = await database.Words.SingleAsync();
        var senseId = (await database.Senses.SingleAsync()).Id;
        before.Status = "approved";
        await database.SaveChangesAsync();
        source["headword"] = "nevera nueva";
        source["status"] = "draft";
        source["topics"] = new JsonArray("technology");
        source["alternatives"]![0]!["value"] = "replacement";
        await Write(source);
        Assert.Equal(new[] { 0, 1, 0, 0 }, Counts(await Import()));
        await database.Entry(before).ReloadAsync();
        Assert.Equal(2, before.Version);
        Assert.Equal("pending_review", before.Status);
        Assert.Equal("nevera nueva", before.Headword);
        Assert.Equal(senseId, (await database.Senses.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(2, await database.WordRevisions.CountAsync());
        Assert.Equal("technology", (await database.WordTopics.SingleAsync()).Topic);
        Assert.Equal("replacement", (await database.WordAlternatives.OrderBy(alternative => alternative.Rank).FirstAsync()).Value);
    }

    [Fact]
    public async Task CanonicalHashIgnoresOrderWhitespaceEscapesAndReviewMetadataButDoesNotDemoteUnchangedWord()
    {
        var source = Seed();
        await Write(source);
        await Import();
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync();
        word.Status = "approved";
        await database.SaveChangesAsync();
        source["status"] = "rejected";
        source["updatedAt"] = "2026-10-18T00:00:00Z";
        source["review"] = new JsonObject { ["reviewedBy"] = "owner" };
        var reversed = new JsonObject(source.AsObject().Reverse().Select(property =>
            new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
        var path = Path.Combine(_words, word.Key + ".json");
        await File.WriteAllTextAsync(path, " \n" + reversed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("nevera", "\\u006eevera", StringComparison.Ordinal));
        Assert.Equal(new[] { 0, 0, 1, 0 }, Counts(await Import()));
        await database.Entry(word).ReloadAsync();
        Assert.Equal("approved", word.Status);
        Assert.Equal(1, word.Version);
        Assert.Single(await database.WordRevisions.ToListAsync());
    }

    [Theory]
    [InlineData("draft", "draft")]
    [InlineData("pending_review", "pending_review")]
    [InlineData("approved", "pending_review")]
    [InlineData("rejected", "pending_review")]
    [InlineData("needs_change", "pending_review")]
    public async Task OnlyDraftAndPendingReviewAreHonoredOnCreation(string fileStatus, string expected)
    {
        var source = Seed();
        source["status"] = fileStatus;
        await Write(source);
        await Import();
        await using var database = _settings.CreateContext();
        Assert.Equal(expected, (await database.Words.SingleAsync()).Status);
        Assert.Equal(expected, JsonNode.Parse((await database.WordRevisions.SingleAsync()).SnapshotJson)!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("unknown-root")]
    [InlineData("unknown-sense")]
    [InlineData("noun-without-article")]
    [InlineData("noun-gender")]
    [InlineData("phrase-gender")]
    [InlineData("wrong-type")]
    [InlineData("too-long")]
    [InlineData("duplicate-topic")]
    [InlineData("bad-date")]
    [InlineData("bad-id")]
    [InlineData("empty-answers")]
    [InlineData("bad-accents")]
    public async Task SchemaFailuresRejectEntireBatchBeforeDatabaseCreation(string defect)
    {
        await Write(Seed());
        var bad = Seed();
        bad["id"] = "es-invalid-01";
        switch (defect)
        {
            case "missing-required": bad.AsObject().Remove("headword"); break;
            case "unknown-root": bad["unknown"] = true; break;
            case "unknown-sense": bad["senses"]![0]!["unknown"] = true; break;
            case "noun-without-article": bad.AsObject().Remove("article"); break;
            case "noun-gender": bad["gender"] = "not_applicable"; break;
            case "phrase-gender": bad["partOfSpeech"] = "phrase"; bad["gender"] = "masculine"; break;
            case "wrong-type": bad["headword"] = 42; break;
            case "too-long": bad["headword"] = new string('x', 61); break;
            case "duplicate-topic": bad["topics"] = new JsonArray("food", "food"); break;
            case "bad-date": bad["createdAt"] = "yesterday"; break;
            case "bad-id": bad["id"] = "INVALID"; break;
            case "empty-answers": bad["senses"]![0]!["acceptedEn"] = new JsonArray(); break;
            case "bad-accents": bad["senses"]![0]!["accentsAreThePoint"] = "true"; break;
        }
        await Write(bad);
        var result = await Import();
        Assert.Equal(new[] { 0, 0, 0, 1 }, Counts(result));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_settings.FilePath)));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("filename")]
    [InlineData("duplicate-property")]
    public async Task InvalidBatchDoesNotChangeExistingContentOrRevisions(string defect)
    {
        var source = Seed();
        await Write(source);
        await Import();
        source["headword"] = "changed";
        await Write(source);
        var json = defect switch
        {
            "malformed" => "{broken",
            "filename" => Seed().ToJsonString(),
            _ => Seed().ToJsonString().Replace("\"headword\":", "\"id\":\"es-duplicate-01\",\"headword\":", StringComparison.Ordinal)
        };
        await File.WriteAllTextAsync(Path.Combine(_words, "zzz-bad.json"), json);
        Assert.Equal(new[] { 0, 0, 0, 1 }, Counts(await Import()));
        await using var database = _settings.CreateContext();
        Assert.Equal(Seed()["headword"]!.GetValue<string>(), (await database.Words.SingleAsync()).Headword);
        Assert.Single(await database.WordRevisions.ToListAsync());
    }

    [Fact]
    public async Task PartialDirectoryNeverRetiresMissingWords()
    {
        var corpusSize = CorpusFileCount();
        await new ContentImporter(_settings).ImportAsync(CorpusDirectory(), false, CancellationToken.None);
        await Write(Seed());
        Assert.Equal(new[] { 0, 0, 1, 0 }, Counts(await Import()));
        await using var database = _settings.CreateContext();
        Assert.Equal(corpusSize, await database.Words.CountAsync());
        Assert.Equal(0, await database.Words.CountAsync(word => word.Status == "retired"));
    }

    [Fact]
    public async Task DryRunCountsNewAndChangedWordsWithoutWritingAnything()
    {
        var source = Seed();
        await Write(source);
        Assert.Equal(new[] { 1, 0, 0, 0 }, Counts(await Import(true)));
        Assert.False(File.Exists(_settings.FilePath));
        await Import();
        Assert.Equal(new[] { 0, 0, 1, 0 }, Counts(await Import(true)));
        source["notes"] = "changed";
        await Write(source);
        Assert.Equal(new[] { 0, 1, 0, 0 }, Counts(await Import(true)));
        await using var database = _settings.CreateContext();
        Assert.Equal(1, (await database.Words.SingleAsync()).Version);
        Assert.Single(await database.WordRevisions.ToListAsync());
    }

    private Task<ContentImportResult> Import(bool dryRun = false) => new ContentImporter(_settings).ImportAsync(_words, dryRun, CancellationToken.None);
    private static int[] Counts(ContentImportResult result) => [result.Created, result.Updated, result.Unchanged, result.Rejected.Count];
    private async Task Write(JsonNode source) => await File.WriteAllTextAsync(Path.Combine(_words, source["id"]!.GetValue<string>() + ".json"), source.ToJsonString());
    internal static JsonNode Seed() => JsonNode.Parse(File.ReadAllText(Path.Combine(CorpusDirectory(), "es-nevera-01.json")))!;

    // The corpus grows with every content batch, so expectations are derived from the files on disk.
    // A frozen count here is a tripwire for the next batch, not a property of the importer.
    internal static string[] CorpusFiles()
    {
        var files = Directory.GetFiles(CorpusDirectory(), "*.json");
        Assert.NotEmpty(files); // a vanished corpus must fail loudly instead of passing vacuously
        return files;
    }

    internal static int CorpusFileCount() => CorpusFiles().Length;

    private static string CorpusKey(string path) => Path.GetFileNameWithoutExtension(path);

    internal static string CorpusDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "content", "words");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate content/words test corpus.");
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
