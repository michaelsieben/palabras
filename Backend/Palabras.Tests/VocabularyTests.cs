using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Palabras.Accounts;
using Palabras.Persistence;
using Palabras.Vocabulary;
using Xunit;

namespace Palabras.Tests;

public sealed class VocabularyTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "palabras-words-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;
    private readonly string _content;
    private static readonly string Repo = FindRepo();

    public VocabularyTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_root, "palabras.db"));
        _content = Path.Combine(_root, "content");
        Directory.CreateDirectory(_content);
    }

    [Fact]
    public async Task EmbeddedSchemaIsExactlyAuthoritativeSchema()
    {
        using var original = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Repo, "content", "schema", "word.schema.json"), CancellationToken.None));
        using var embedded = JsonDocument.Parse(WordSchema.SchemaText);
        Assert.True(JsonElement.DeepEquals(original.RootElement, embedded.RootElement));
    }

    [Fact]
    public async Task RealSeedImportCreatesRelationsAndReimportChangesNothing()
    {
        var path = Path.Combine(Repo, "content", "words");
        var first = await Run(path);
        Assert.Equal(0, first.Exit);
        Assert.Contains("Import: created=58 updated=0 skipped=0 failed=0 files=58", first.Output);
        await using var database = _settings.CreateContext();
        var words = await database.Words.AsNoTracking().OrderBy(word => word.Id).ToListAsync(CancellationToken.None);
        var senses = await database.Senses.AsNoTracking().OrderBy(sense => sense.Id).ToListAsync(CancellationToken.None);
        var alternatives = await database.Alternatives.AsNoTracking().OrderBy(alternative => alternative.Id).ToListAsync(CancellationToken.None);
        Assert.Equal(58, words.Count);
        Assert.All(words, word => Assert.Equal("pending_review", word.Status));
        Assert.Equal(62, senses.Count);
        Assert.Equal(173, alternatives.Count);
        Assert.All(senses, sense => Assert.Contains(words, word => word.Id == sense.WordId));
        Assert.All(alternatives, alternative => Assert.Contains(words, word => word.Id == alternative.WordId));
        var coger = words.Single(word => word.ExternalId == "es-coger-01");
        Assert.Equal(2, senses.Count(sense => sense.WordId == coger.Id));
        Assert.Equal(3, alternatives.Count(alternative => alternative.WordId == coger.Id));
        Assert.Empty(await database.WordsForSession().ToListAsync(CancellationToken.None));
        var second = await Run(path);
        Assert.Equal(0, second.Exit);
        Assert.Contains("Import: created=0 updated=0 skipped=58 failed=0 files=58", second.Output);
        var after = await database.Words.AsNoTracking().OrderBy(word => word.Id).ToListAsync(CancellationToken.None);
        Assert.Equal(words.Select(word => (word.Id, word.ContentJson, word.CreatedAt, word.UpdatedAt)), after.Select(word => (word.Id, word.ContentJson, word.CreatedAt, word.UpdatedAt)));
        Assert.Equal(senses.Select(sense => sense.Id), await database.Senses.OrderBy(sense => sense.Id).Select(sense => sense.Id).ToListAsync(CancellationToken.None));
        Assert.Equal(alternatives.Select(alternative => alternative.Id), await database.Alternatives.OrderBy(alternative => alternative.Id).Select(alternative => alternative.Id).ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpsertUsesExternalIdNotHeadwordAndPreservesChildrenAndRejectedAlternatives()
    {
        var entry = Seed();
        var file = await Write(entry);
        Assert.Equal(0, (await Run(file)).Exit);
        await using var database = _settings.CreateContext();
        var before = await database.Words.AsNoTracking().Include(word => word.Senses).Include(word => word.Alternatives).SingleAsync(CancellationToken.None);
        entry["headword"] = "tomar";
        entry["status"] = "rejected";
        entry["updatedAt"] = "2026-10-18T12:00:00Z";
        entry["senses"]![0]!["notes"] = "Peninsular variant-specific note.";
        entry["review"] = new JsonObject { ["rejectionReason"] = "Reviewer chose another wording." };
        await Write(entry);
        var result = await Run(file);
        Assert.Contains("created=0 updated=1 skipped=0 failed=0", result.Output);
        var after = await database.Words.AsNoTracking().Include(word => word.Senses).Include(word => word.Alternatives).SingleAsync(CancellationToken.None);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Senses.Select(sense => sense.Id).Order(), after.Senses.Select(sense => sense.Id).Order());
        Assert.Equal(before.Alternatives.Select(alternative => alternative.Id).Order(), after.Alternatives.Select(alternative => alternative.Id).Order());
        Assert.Equal("rejected", after.Status);
        Assert.Equal("Peninsular variant-specific note.", after.Senses.Single(sense => sense.Position == 0).Notes);
        Assert.Equal(3, after.Alternatives.Count);
        Assert.Contains("Reviewer chose another wording.", after.ContentJson);
        Assert.Empty(await database.WordsForSession().ToListAsync(CancellationToken.None));
        Assert.Contains("skipped=1", (await Run(file)).Output);
    }

    [Fact]
    public async Task EquivalentJsonFormattingAndPropertyOrderDoesNotWriteAgain()
    {
        var entry = Seed();
        var file = await Write(entry);
        Assert.Equal(0, (await Run(file)).Exit);
        var reordered = new JsonObject(entry.Reverse().Select(property => new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
        await File.WriteAllTextAsync(file, reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        Assert.Contains("created=0 updated=0 skipped=1 failed=0", (await Run(file)).Output);
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-sense")]
    [InlineData("unknown-alternative")]
    [InlineData("unknown-region")]
    [InlineData("unknown-provenance")]
    [InlineData("unknown-review")]
    [InlineData("unknown-lint")]
    [InlineData("status")]
    [InlineData("kind")]
    [InlineData("one-alternative")]
    [InlineData("five-alternatives")]
    [InlineData("no-senses")]
    [InlineData("region")]
    [InlineData("scope")]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("duplicate-topic")]
    [InlineData("topic")]
    [InlineData("date")]
    [InlineData("length")]
    [InlineData("noun")]
    [InlineData("phrase")]
    [InlineData("vulgar")]
    public async Task InvalidBatchFailsWithoutAnyPartialWrite(string invalid)
    {
        var valid = Seed();
        await Write(valid);
        var entry = Seed();
        entry["id"] = "es-invalid-99";
        switch (invalid)
        {
            case "unknown-root": entry["surprise"] = true; break;
            case "unknown-sense": entry["senses"]![0]!["surprise"] = true; break;
            case "unknown-alternative": entry["alternatives"]![0]!["surprise"] = true; break;
            case "unknown-region": entry["region"]!["surprise"] = true; break;
            case "unknown-provenance": entry["provenance"]!["surprise"] = true; break;
            case "unknown-review": entry["review"] = new JsonObject { ["surprise"] = true }; break;
            case "unknown-lint": entry["lint"] = new JsonObject { ["surprise"] = true }; break;
            case "status": entry["status"] = "active"; break;
            case "kind": entry["alternatives"]![0]!["kind"] = "generated"; break;
            case "one-alternative": ((JsonArray)entry["alternatives"]!).RemoveAt(2); ((JsonArray)entry["alternatives"]!).RemoveAt(1); break;
            case "five-alternatives": ((JsonArray)entry["alternatives"]!).Add(entry["alternatives"]![0]!.DeepClone()); ((JsonArray)entry["alternatives"]!).Add(entry["alternatives"]![0]!.DeepClone()); break;
            case "no-senses": entry["senses"] = new JsonArray(); break;
            case "region": entry["region"]!["variant"] = "es-419"; break;
            case "scope": entry["region"]!["scope"] = "global"; break;
            case "missing": entry.Remove("provenance"); break;
            case "type": entry["headword"] = 42; break;
            case "duplicate-topic": entry["topics"] = new JsonArray("food", "food"); break;
            case "topic": entry["topics"] = new JsonArray("invented"); break;
            case "date": entry["createdAt"] = "yesterday"; break;
            case "length": entry["headword"] = new string('a', 61); break;
            case "noun": entry["partOfSpeech"] = "noun"; break;
            case "phrase": entry["partOfSpeech"] = "phrase"; entry["gender"] = "masculine"; break;
            case "vulgar": entry["register"] = "vulgar"; entry.Remove("registerNotes"); break;
        }
        await Write(entry);
        var result = await Run(_content);
        Assert.Equal(1, result.Exit);
        Assert.Contains("created=0 updated=0 skipped=1 failed=1 files=2", result.Output);
        Assert.Contains("FAIL es-invalid-99.json", result.Error);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("rejected")]
    public async Task ImportCannotPromoteDraftOrRejectedAndRollsBackWholeBatch(string status)
    {
        var existing = Seed(); existing["status"] = status;
        var file = await Write(existing);
        Assert.Equal(0, (await Run(file)).Exit);
        existing["status"] = "approved"; await Write(existing);
        var other = Seed(); other["id"] = "es-another-99"; await Write(other);
        Assert.Equal(1, (await Run(_content)).Exit);
        await using var database = _settings.CreateContext();
        Assert.Equal(status, (await database.Words.SingleAsync(CancellationToken.None)).Status);
        Assert.Equal(3, await database.Alternatives.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WordsForSessionSelectsOnlyApprovedAcrossAllStatuses()
    {
        foreach (var status in new[] { "draft", "pending_review", "approved", "rejected", "needs_change" })
        {
            var entry = Seed(); entry["id"] = "es-" + status.Replace('_', '-') + "-99"; entry["status"] = status;
            await Write(entry);
        }
        Assert.Equal(0, (await Run(_content)).Exit);
        await using var database = _settings.CreateContext();
        Assert.Equal(5, await database.Words.CountAsync(CancellationToken.None));
        Assert.Equal("approved", (await database.WordsForSession().SingleAsync(CancellationToken.None)).Status);
        Assert.Contains("approved", database.WordsForSession().ToQueryString());
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => database.Database.ExecuteSqlRawAsync("UPDATE words SET status='invented'", CancellationToken.None));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => database.Database.ExecuteSqlRawAsync("UPDATE alternatives SET kind='invented'", CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedCatalogFiltersPagesAndReturnsSchemaSensesAndAlternatives()
    {
        Assert.Equal(0, (await Run(Path.Combine(Repo, "content", "words"))).Exit);
        await using (var database = _settings.CreateContext())
        {
            await new AccountService(database, new AccountPasswordHasher()).CreateAsync("owner", "learner", "vocabulary test password", CancellationToken.None);
        }
        using var factory = new VocabularyFactory(_root, _settings.FilePath);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        foreach (var route in new[] { "/api/words", "/api/words/es-coger-01" })
        {
            using var anonymous = await client.GetAsync(route, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.False(anonymous.Headers.Contains("Location"));
        }
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username = "learner", password = "vocabulary test password" }) };
        loginRequest.Headers.Add("X-Palabras", "1");
        using var login = await client.SendAsync(loginRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        using var response = await client.GetAsync("/api/words?status=pending_review&level=A1&topic=transport&partOfSpeech=verb&page=1&pageSize=1", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("A1", item.GetProperty("cefr").GetString());
        Assert.Equal("pending_review", item.GetProperty("status").GetString());
        Assert.Equal("verb", item.GetProperty("partOfSpeech").GetString());
        Assert.Contains(item.GetProperty("topics").EnumerateArray(), topic => topic.GetString() == "transport");
        using var single = await client.GetAsync("/api/words/es-coger-01", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using var body = JsonDocument.Parse(await single.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(2, body.RootElement.GetProperty("senses").GetArrayLength());
        Assert.Equal(3, body.RootElement.GetProperty("alternatives").GetArrayLength());
        Assert.Equal("synonym", body.RootElement.GetProperty("alternatives")[0].GetProperty("kind").GetString());
        Assert.True(body.RootElement.GetProperty("alternatives")[0].GetProperty("reason").GetString()!.Length >= 8);
        WordSchema.Validate(body.RootElement);
        await using (var database = _settings.CreateContext())
        {
            var word = await database.Words.Include(word => word.Alternatives).SingleAsync(word => word.ExternalId == "es-coger-01", CancellationToken.None);
            word.Status = "rejected";
            word.Alternatives.Single(alternative => alternative.Position == 0).Reason = "Reviewer retained this alternative for later.";
            await database.SaveChangesAsync(CancellationToken.None);
        }
        using var changed = await client.GetAsync("/api/words/es-coger-01", CancellationToken.None);
        using var changedBody = JsonDocument.Parse(await changed.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("rejected", changedBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("Reviewer retained this alternative for later.", changedBody.RootElement.GetProperty("alternatives")[0].GetProperty("reason").GetString());
        using var approved = await client.GetAsync("/api/words?status=approved", CancellationToken.None);
        using var approvedBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(0, approvedBody.RootElement.GetProperty("total").GetInt32());
        foreach (var route in new[] { "/api/words/es-missing-99", "/api/unknown" })
        {
            using var missing = await client.GetAsync(route, CancellationToken.None);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using var fallback = await client.GetAsync("/future-route", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        using var badPaging = await client.GetAsync("/api/words?pageSize=101", CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, badPaging.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonDuplicateFieldsAndFilenameMismatchFail()
    {
        var file = await Write(Seed());
        await File.WriteAllTextAsync(file, "{", CancellationToken.None);
        Assert.Equal(1, (await Run(file)).Exit);
        await File.WriteAllTextAsync(file, Seed().ToJsonString().Replace("\"headword\":", "\"headword\":\"duplicate\",\"headword\":", StringComparison.Ordinal), CancellationToken.None);
        Assert.Equal(1, (await Run(file)).Exit);
        await File.WriteAllTextAsync(Path.Combine(_content, "wrong.json"), Seed().ToJsonString(), CancellationToken.None);
        Assert.Equal(1, (await Run(Path.Combine(_content, "wrong.json"))).Exit);
        Assert.False(File.Exists(_settings.FilePath));
    }

    private async Task<(int Exit, string Output, string Error)> Run(string path)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await ImportCli.RunAsync(["import", path], _settings, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<string> Write(JsonObject entry)
    {
        var path = Path.Combine(_content, entry["id"]!.GetValue<string>() + ".json");
        await File.WriteAllTextAsync(path, entry.ToJsonString(), CancellationToken.None);
        return path;
    }

    private static JsonObject Seed() => JsonNode.Parse(File.ReadAllText(Path.Combine(Repo, "content", "words", "es-coger-01.json")))!.AsObject();
    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "content", "schema", "word.schema.json"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository content not found.");
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }

    private sealed class VocabularyFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly string _databasePath;
        internal VocabularyFactory(string root, string databasePath)
        {
            _root = root; _databasePath = databasePath;
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html>Vocabulary test shell</html>");
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
        }
    }
}
