using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Palabras.Persistence;
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

// M4 reviewer gate schema (design-amendment.md ΔF:609-618): the word_reviews table, the nullable
// council/lint columns on words, and 'demote' on word_revisions. The upgrade test is the important
// one: the migration must add to a database that is already live at the previous migration.
public sealed class M4ReviewSchemaTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesToReviewGateSchemaAtStartupAsync()
    {
        using var factory = new ReviewApplicationFactory();
        Assert.False(File.Exists(factory.DatabasePath));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var database = new DatabaseSettings(factory.DatabasePath).CreateContext();
        Assert.Contains(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None), name => name.EndsWith("_ReviewGate", StringComparison.Ordinal));
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        DbConnection connection = database.Database.GetDbConnection();

        // name:type:not-null:default:primary-key-order, from the migrated database.
        Assert.Equal([
            "id:INTEGER:1::1", "word_id:INTEGER:1::0", "word_version:INTEGER:1::0", "sense_order:INTEGER:0::0",
            "reviewer_id:INTEGER:1::0", "action:TEXT:1::0", "field:TEXT:0::0", "chosen_alternative_id:INTEGER:0::0",
            "edit_diff:TEXT:0::0", "note:TEXT:0::0", "client_review_id:TEXT:1::0", "via_batch:INTEGER:1:0:0",
            "council_run_id:INTEGER:0::0", "agreement_at_review:REAL:0::0", "decision_ms:INTEGER:0::0",
            "created_at:INTEGER:1::0"],
            await ReadStringsAsync(connection, "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name) ORDER BY cid;", "word_reviews", CancellationToken.None));

        string[] wordColumns = await ReadStringsAsync(connection,
            "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name);", "words", CancellationToken.None);
        foreach (string column in new[]
        {
            "council_agreement:REAL:0::0", "council_band:TEXT:0::0", "reduced_confidence:INTEGER:1:0:0",
            "lint_warnings:TEXT:0::0", "status_changed_at:INTEGER:0::0", "approved_version:INTEGER:0::0"
        })
        {
            Assert.Contains(column, wordColumns);
        }
        // The six new columns are the only change: 20 columns before ReviewGate, 26 after.
        Assert.Equal(26, wordColumns.Length);

        string reviewsSql = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "word_reviews", CancellationToken.None));
        Assert.Contains("CHECK (action IN ('approve', 'approve_alt', 'approve_edit', 'reject', 'needs_change', 'demote'))", reviewsSql);
        Assert.Contains("CHECK (field IN ('lemma', 'gloss', 'example', 'regional_note', 'regional_variant', 'register_variant', 'article', 'gender', 'accepted', 'whole'))", reviewsSql);
        string wordsSql = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "words", CancellationToken.None));
        Assert.Contains("CHECK (council_band IN ('alta', 'media'))", wordsSql);
        Assert.Contains("CHECK (article IN ('el', 'la', 'los', 'las'))", wordsSql);
        Assert.Contains("CHECK (gender IN ('masculine', 'feminine', 'common', 'not_applicable'))", wordsSql);
        Assert.Contains("CHECK (status IN ('draft', 'pending_review', 'approved', 'needs_change', 'rejected', 'retired'))", wordsSql);
        string revisionsSql = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "word_revisions", CancellationToken.None));
        Assert.Contains("CHECK (action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire', 'demote'))", revisionsSql);

        Assert.Equal(["CREATE UNIQUE INDEX \"IX_word_reviews_client_review_id\" ON \"word_reviews\" (\"client_review_id\")"],
            await ReadStringsAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", "IX_word_reviews_client_review_id", CancellationToken.None));
        Assert.Equal(["CREATE INDEX \"IX_word_reviews_word_id_word_version\" ON \"word_reviews\" (\"word_id\", \"word_version\")"],
            await ReadStringsAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", "IX_word_reviews_word_id_word_version", CancellationToken.None));

        Assert.Equal(["chosen_alternative_id:word_alternatives:id:SET NULL", "reviewer_id:users:id:NO ACTION", "word_id:words:id:CASCADE"],
            (await ReadStringsAsync(connection, "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);", "word_reviews", CancellationToken.None)).Order().ToArray());
    }

    [Fact]
    public async Task UpgradeFromPreviousMigrationAddsWithoutNarrowingAndIsRerunSafeAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("RolloverCutoverMarker", CancellationToken.None);
        await SeedAsync(database);
        DbConnection connection = database.Database.GetDbConnection();
        Dictionary<string, string> columnsBefore = await ColumnsAsync(connection, "words");
        string[] revisionsBefore = await ReadStringsAsync(connection,
            "SELECT action || ':' || version FROM word_revisions ORDER BY id;", "", CancellationToken.None);

        await migrator.MigrateAsync(cancellationToken: CancellationToken.None);

        // Additive only: every pre-existing words column keeps its name, type, nullability and default.
        Dictionary<string, string> columnsAfter = await ColumnsAsync(connection, "words");
        foreach (KeyValuePair<string, string> column in columnsBefore)
        {
            Assert.True(columnsAfter.TryGetValue(column.Key, out string? definition), column.Key);
            Assert.Equal(column.Value, definition);
        }
        Assert.Equal(columnsBefore.Keys.Concat(["council_agreement", "council_band", "reduced_confidence", "lint_warnings", "status_changed_at", "approved_version"]).Order().ToArray(),
            columnsAfter.Keys.Order().ToArray());
        Assert.Equal(revisionsBefore, await ReadStringsAsync(connection,
            "SELECT action || ':' || version FROM word_revisions ORDER BY id;", "", CancellationToken.None));
        Word word = await database.Words.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("es-coger-01", word.Key);
        Assert.Equal("approved", word.Status);
        Assert.Equal(1, word.Version);
        Assert.Equal("hash", word.ContentHash);
        Assert.Null(word.CouncilAgreement);
        Assert.Null(word.CouncilBand);
        Assert.Equal(0, word.ReducedConfidence);
        Assert.Null(word.LintWarnings);
        Assert.Null(word.StatusChangedAt);
        Assert.Null(word.ApprovedVersion);
        Assert.Equal("take", (await database.Senses.AsNoTracking().SingleAsync(CancellationToken.None)).EnGloss);
        Assert.Single(await database.WordAlternatives.AsNoTracking().ToListAsync(CancellationToken.None));

        // A second apply is a no-op: same migration history, nothing pending, same schema.
        string schema = string.Join('\n', await ReadStringsAsync(connection,
            "SELECT type || ':' || name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name;", "", CancellationToken.None));
        int applied = (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count();
        await database.Database.MigrateAsync(CancellationToken.None);
        Assert.Equal(applied, (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        Assert.Equal(schema, string.Join('\n', await ReadStringsAsync(connection,
            "SELECT type || ':' || name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name;", "", CancellationToken.None)));

        // No statement drops or renames a column; the SQLite table rebuild only adds columns.
        string script = migrator.GenerateScript("RolloverCutoverMarker", "ReviewGate");
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE \"words\" ADD \"council_band\" TEXT NULL;", script);
        Assert.Contains("CREATE TABLE \"word_reviews\"", script);
        // The rebuild carries the old columns forward explicitly, never a SELECT *.
        Assert.Contains("INSERT INTO \"ef_temp_words\" (\"id\", \"approved_version\"", script);
    }

    [Fact]
    public async Task ReviewConstraintsDefaultsAndCascadesAreEnforcedAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        await SeedAsync(database);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (2,'reviewer','reviewer','hash',1,1);", CancellationToken.None);

        database.WordReviews.Add(new WordReview
        {
            WordId = 1, WordVersion = 1, ReviewerId = 2, Action = "approve",
            ClientReviewId = "review-1", CreatedAt = 1792886400000
        });
        await database.SaveChangesAsync(CancellationToken.None);
        database.ChangeTracker.Clear();
        WordReview review = await database.WordReviews.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(0, review.ViaBatch);
        Assert.Null(review.Field);
        Assert.Null(review.SenseOrder);
        Assert.Null(review.ChosenAlternativeId);
        Assert.Null(review.EditDiff);
        Assert.Null(review.Note);
        Assert.Null(review.CouncilRunId);
        Assert.Null(review.AgreementAtReview);
        Assert.Null(review.DecisionMs);

        const string insertReview = "INSERT INTO word_reviews (word_id,word_version,reviewer_id,action,client_review_id,created_at) VALUES (1,1,2,'approve','review-2',0);";
        await database.Database.ExecuteSqlRawAsync(insertReview, CancellationToken.None);
        // Idempotency: the replay key is unique, so a retried verdict cannot become a second row.
        await AssertConstraintAsync(database, insertReview);
        await AssertConstraintAsync(database, "UPDATE word_reviews SET client_review_id=NULL;");
        await AssertConstraintAsync(database, "UPDATE word_reviews SET action='invalid';");
        await AssertConstraintAsync(database, "UPDATE word_reviews SET field='invalid';");
        await AssertConstraintAsync(database, "UPDATE word_reviews SET word_id=999;");
        await AssertConstraintAsync(database, "UPDATE word_reviews SET reviewer_id=999;");
        await AssertConstraintAsync(database, "UPDATE words SET council_band='baja';");
        foreach (string action in new[] { "approve", "approve_alt", "approve_edit", "reject", "needs_change", "demote" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_reviews SET action={action};", CancellationToken.None);
        }
        foreach (string field in new[] { "lemma", "gloss", "example", "regional_note", "regional_variant", "register_variant", "article", "gender", "accepted", "whole" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_reviews SET field={field};", CancellationToken.None);
        }
        await database.Database.ExecuteSqlRawAsync("UPDATE word_reviews SET field=NULL, action='approve';", CancellationToken.None);

        // The previous revision actions still pass; 'demote' is the only addition.
        foreach (string action in new[] { "import", "approve", "reject", "needs_change", "edit", "retire", "demote" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO word_revisions (word_id, version, action, snapshot_json, created_at) VALUES (1, 1, {action}, '{{}}', 0);",
                CancellationToken.None);
        }
        await AssertConstraintAsync(database, "INSERT INTO word_revisions (word_id, version, action, snapshot_json, created_at) VALUES (1, 1, 'invalid', '{{}}', 0);");

        // A chosen alternative that disappears clears the pointer and keeps the verdict; deleting the
        // word removes its reviews. Both must work under FK enforcement.
        await database.Database.ExecuteSqlRawAsync("UPDATE word_reviews SET chosen_alternative_id=(SELECT min(id) FROM word_alternatives);", CancellationToken.None);
        await database.WordAlternatives.ExecuteDeleteAsync(CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.All(await database.WordReviews.AsNoTracking().ToListAsync(CancellationToken.None), value => Assert.Null(value.ChosenAlternativeId));
        await database.Words.ExecuteDeleteAsync(CancellationToken.None);
        Assert.Empty(await database.WordReviews.ToListAsync(CancellationToken.None));
        Assert.Empty(await database.WordRevisions.ToListAsync(CancellationToken.None));
    }

    private static async Task SeedAsync(PalabrasDbContext database)
    {
        // Raw SQL, not entities: the context model already has the ReviewGate columns while this
        // database is still at the previous migration.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id,kind,value,reason,rank) VALUES (1,'synonym','tomar','context',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_revisions (word_id,version,action,snapshot_json,created_at) VALUES (1,1,'approve','{{}}',1);", CancellationToken.None);
    }

    private static async Task<Dictionary<string, string>> ColumnsAsync(DbConnection connection, string table)
    {
        string[] rows = await ReadStringsAsync(connection,
            "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') FROM pragma_table_info($name);", table, CancellationToken.None);
        return rows.ToDictionary(row => row[..row.IndexOf(':', StringComparison.Ordinal)], row => row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);
    }

    private static async Task AssertConstraintAsync(PalabrasDbContext database, string sql)
    {
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(sql, CancellationToken.None));
        Assert.Equal(19, error.SqliteErrorCode);
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

    private sealed class ReviewApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
