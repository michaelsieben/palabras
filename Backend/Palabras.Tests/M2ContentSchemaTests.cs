using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

public sealed class M2ContentSchemaTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesToFullContentSchemaAtStartupAsync()
    {
        using var factory = new ContentApplicationFactory();
        Assert.False(File.Exists(factory.DatabasePath));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var database = new DatabaseSettings(factory.DatabasePath).CreateContext();
        Assert.Contains(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None), name => name.EndsWith("_ContentSchema", StringComparison.Ordinal));
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        DbConnection connection = database.Database.GetDbConnection();

        // Each entry is name:type:not-null:default:primary-key-order, from the actual migrated DB.
        var expectedColumns = new Dictionary<string, string[]>
        {
            ["words"] = [
                "id:INTEGER:1::1", "key:TEXT:1::0", "headword:TEXT:1::0", "article:TEXT:0::0",
                "gender:TEXT:0::0", "pos:TEXT:1::0", "cefr:TEXT:1::0", "region_scope:TEXT:1::0",
                "register:TEXT:1::0", "register_notes:TEXT:0::0", "region_notes:TEXT:0::0",
                "notes:TEXT:0::0", "plural:TEXT:0::0", "status:TEXT:1::0", "version:INTEGER:1:1:0",
                "content_hash:TEXT:1::0", "provenance_method:TEXT:1::0", "provenance_run:TEXT:0::0",
                "created_at:INTEGER:1::0", "updated_at:INTEGER:1::0"],
            ["senses"] = [
                "id:INTEGER:1::1", "word_id:INTEGER:1::0", "sense_order:INTEGER:1::0", "en_gloss:TEXT:1::0",
                "en_accepted:TEXT:1:'[]':0", "es_accepted:TEXT:1:'[]':0", "example_es:TEXT:1::0",
                "example_en:TEXT:1::0", "accents_are_the_point:INTEGER:1:0:0", "notes:TEXT:0::0"],
            ["word_alternatives"] = [
                "id:INTEGER:1::1", "word_id:INTEGER:1::0", "kind:TEXT:1::0", "value:TEXT:1::0",
                "gloss:TEXT:0::0", "reason:TEXT:1::0", "target_key:TEXT:0::0", "example:TEXT:0::0",
                "rank:INTEGER:1::0"],
            ["word_topics"] = ["word_id:INTEGER:1::1", "topic:TEXT:1::2"],
            ["word_revisions"] = [
                "id:INTEGER:1::1", "word_id:INTEGER:1::0", "version:INTEGER:1::0", "actor_user_id:INTEGER:0::0",
                "action:TEXT:1::0", "snapshot_json:TEXT:1::0", "created_at:INTEGER:1::0"]
        };
        foreach (KeyValuePair<string, string[]> table in expectedColumns)
        {
            Assert.Equal(table.Value, await ReadStringsAsync(connection,
                "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name) ORDER BY cid;",
                table.Key, CancellationToken.None));
        }

        string wordsSql = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "words", CancellationToken.None));
        Assert.Contains("CHECK (article IN ('el', 'la', 'los', 'las'))", wordsSql);
        Assert.Contains("CHECK (gender IN ('masculine', 'feminine', 'common', 'not_applicable'))", wordsSql);
        Assert.Contains("CHECK (status IN ('draft', 'pending_review', 'approved', 'needs_change', 'rejected', 'retired'))", wordsSql);
        string revisionsSql = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "word_revisions", CancellationToken.None));
        Assert.Contains("CHECK (action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire'))", revisionsSql);

        Assert.Equal(["CREATE UNIQUE INDEX \"IX_words_key\" ON \"words\" (\"key\")"],
            await ReadStringsAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", "IX_words_key", CancellationToken.None));
        Assert.Equal(["CREATE INDEX \"IX_words_status\" ON \"words\" (\"status\")"],
            await ReadStringsAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", "IX_words_status", CancellationToken.None));
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_senses_word_id_sense_order\" ON \"senses\" (\"word_id\", \"sense_order\")"],
            await ReadStringsAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", "IX_senses_word_id_sense_order", CancellationToken.None));

        foreach (string table in new[] { "senses", "word_alternatives", "word_topics", "word_revisions" })
        {
            Assert.Contains("word_id:words:id:CASCADE", await ReadStringsAsync(connection,
                "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);", table, CancellationToken.None));
        }
        Assert.Contains("actor_user_id:users:id:NO ACTION", await ReadStringsAsync(connection,
            "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);", "word_revisions", CancellationToken.None));
    }

    [Fact]
    public async Task ContentConstraintsAndDefaultsAreEnforcedAsync()
    {
        using var factory = new ContentApplicationFactory();
        using var client = factory.CreateClient();
        await using var database = new DatabaseSettings(factory.DatabasePath).CreateContext();
        var word = new Word { Key = "es-coger-01", Headword = "coger", Pos = "verb", Cefr = "A1", RegionScope = "ES", Register = "neutral", ContentHash = "hash", ProvenanceMethod = "manual" };
        database.Words.Add(word);
        await database.SaveChangesAsync(CancellationToken.None);
        Assert.Null(word.Plural);
        Assert.Equal(1, word.Version);

        foreach (string sql in new[] { "UPDATE words SET article = 'invalid';", "UPDATE words SET gender = 'invalid';", "UPDATE words SET status = 'invalid';" })
        {
            SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(
                sql, CancellationToken.None));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (word_id, sense_order, en_gloss, example_es, example_en) SELECT id, 1, 'take', 'Lo cojo.', 'I take it.' FROM words;", CancellationToken.None);
        Sense sense = await database.Senses.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("[]", sense.EnAccepted);
        Assert.Equal("[]", sense.EsAccepted);
        Assert.False(sense.AccentsAreThePoint);
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (word_id, sense_order, en_gloss, example_es, example_en) SELECT id, 1, 'take', 'Lo cojo.', 'I take it.' FROM words;", CancellationToken.None));
        await database.Database.ExecuteSqlRawAsync("INSERT INTO word_topics (word_id, topic) SELECT id, 'daily' FROM words;", CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_topics (word_id, topic) SELECT id, 'daily' FROM words;", CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_revisions (word_id, version, action, snapshot_json, created_at) SELECT id, 1, 'invalid', '{{}}', 0 FROM words;", CancellationToken.None));
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_revisions (word_id, version, action, snapshot_json, created_at) SELECT id, 1, 'import', '{{}}', 0 FROM words;", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id, kind, value, reason, rank) SELECT id, 'synonym', 'tomar', 'context', 1 FROM words;", CancellationToken.None);
        await database.Words.ExecuteDeleteAsync(CancellationToken.None);
        Assert.Empty(await database.Senses.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.WordTopics.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.WordAlternatives.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.WordRevisions.ToListAsync(CancellationToken.None));
    }

    private static async Task<string[]> ReadStringsAsync(DbConnection connection, string sql, string name, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        var values = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }

    private sealed class ContentApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Unlike M1's seeded factory, do not migrate here: startup must create the schema.
            Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
