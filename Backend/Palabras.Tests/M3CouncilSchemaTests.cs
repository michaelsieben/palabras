using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Palabras.Persistence;
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

// M3b council import schema (design-amendment.md ΔF:556-583). The two upgrade tests are the
// important ones: the migration must add to a live database at the previous migration, must not
// rebuild words/word_alternatives (docs/08-upgrade-rollback.md §G documents that crash window), and
// must leave every new NOT NULL column defaulted so the old image can still INSERT after a rollback.
public sealed class M3CouncilSchemaTests
{
    private static readonly string[] WordColumnsAdded = ["council_run_id", "difficulty", "source"];

    private static readonly string[] AlternativeColumnsAdded =
        ["field", "models", "region", "register", "source", "support", "word_version"];

    [Fact]
    public async Task UpgradeFromReviewGateIsAdditiveAndRerunSafeAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("ReviewGate", CancellationToken.None);
        await SeedAsync(database);
        DbConnection connection = database.Database.GetDbConnection();
        Dictionary<string, string> wordsBefore = await ColumnsAsync(connection, "words");
        Dictionary<string, string> alternativesBefore = await ColumnsAsync(connection, "word_alternatives");
        string[] wordsBeforeRows = await ReadStringsAsync(connection,
            "SELECT key || ':' || status || ':' || version || ':' || content_hash FROM words ORDER BY id;", "", CancellationToken.None);

        await migrator.MigrateAsync(cancellationToken: CancellationToken.None);

        // Additive only: every pre-existing column keeps its name, type, nullability and default.
        Dictionary<string, string> wordsAfter = await ColumnsAsync(connection, "words");
        foreach (KeyValuePair<string, string> column in wordsBefore)
        {
            Assert.True(wordsAfter.TryGetValue(column.Key, out string? definition), column.Key);
            Assert.Equal(column.Value, definition);
        }
        Assert.Equal(wordsBefore.Keys.Concat(WordColumnsAdded).Order().ToArray(), wordsAfter.Keys.Order().ToArray());
        // 29 = the 28 CouncilImport columns plus WordDifficulty's nullable difficulty.
        Assert.Equal(29, wordsAfter.Count);

        Dictionary<string, string> alternativesAfter = await ColumnsAsync(connection, "word_alternatives");
        foreach (KeyValuePair<string, string> column in alternativesBefore)
        {
            Assert.True(alternativesAfter.TryGetValue(column.Key, out string? definition), column.Key);
            Assert.Equal(column.Value, definition);
        }
        Assert.Equal(alternativesBefore.Keys.Concat(AlternativeColumnsAdded).Order().ToArray(), alternativesAfter.Keys.Order().ToArray());
        Assert.Equal(16, alternativesAfter.Count);

        // Seeded rows survive the ALTERs, and the new columns take their defaults on existing rows.
        Assert.Equal(wordsBeforeRows, await ReadStringsAsync(connection,
            "SELECT key || ':' || status || ':' || version || ':' || content_hash FROM words ORDER BY id;", "", CancellationToken.None));
        Word word = await database.Words.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("manual", word.Source);
        Assert.Null(word.CouncilRunId);
        WordAlternative alternative = await database.WordAlternatives.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("synonym", alternative.Kind);
        Assert.Equal("tomar", alternative.Value);
        Assert.Null(alternative.Field);
        Assert.Null(alternative.Models);
        Assert.Null(alternative.Region);
        Assert.Null(alternative.Register);
        Assert.Equal(0, alternative.Support);
        Assert.Equal(0, alternative.WordVersion);
        Assert.Equal("manual", alternative.Source);
        Assert.Single(await database.Senses.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Single(await database.Cards.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Single(await database.WordReviews.AsNoTracking().ToListAsync(CancellationToken.None));

        // A second apply is a no-op: same migration history, nothing pending, same schema.
        string schema = await SchemaAsync(connection);
        int applied = (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count();
        await database.Database.MigrateAsync(CancellationToken.None);
        Assert.Equal(applied, (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        Assert.Equal(schema, await SchemaAsync(connection));

        // No statement rebuilds a table: no ef_temp_*, no FK-suppression window, no drop or rename.
        string script = migrator.GenerateScript("ReviewGate", "CouncilImport");
        Assert.DoesNotContain("ef_temp_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA foreign_keys", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE \"words\" ADD COLUMN \"source\" TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('council', 'manual', 'reviewer'));", script);
        Assert.Contains("ALTER TABLE \"words\" ADD COLUMN \"council_run_id\" INTEGER NULL REFERENCES \"council_runs\" (\"id\");", script);
        Assert.Contains("ALTER TABLE \"word_alternatives\" ADD COLUMN \"word_version\" INTEGER NOT NULL DEFAULT 0;", script);
        // The principal table is created before the ALTER that references it.
        Assert.True(script.IndexOf("CREATE TABLE \"council_runs\"", StringComparison.Ordinal)
            < script.IndexOf("ADD COLUMN \"council_run_id\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyInsertStatementsStillSucceedAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);

        // The exact column lists an image built before this migration uses. A rollback keeps the new
        // database, so these must still succeed and take the documented defaults.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id,kind,value,reason,rank) VALUES (1,'synonym','tomar','context',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,1);", CancellationToken.None);

        database.ChangeTracker.Clear();
        Word word = await database.Words.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("manual", word.Source);
        Assert.Null(word.CouncilRunId);
        WordAlternative alternative = await database.WordAlternatives.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Null(alternative.Field);
        Assert.Equal(0, alternative.Support);
        Assert.Equal(0, alternative.WordVersion);
        Assert.Equal("manual", alternative.Source);
    }

    [Fact]
    public async Task EmptyDatabaseMigratesToCouncilSchemaAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        DbConnection connection = database.Database.GetDbConnection();

        // name:type:not-null:default:primary-key-order, from the migrated database.
        Assert.Equal([
            "id:INTEGER:1::1", "run_id:TEXT:1::0", "mode:TEXT:1::0", "prompt_versions:TEXT:1::0",
            "style_guide_sha256:TEXT:1::0", "topic:TEXT:0::0", "cefr:TEXT:0::0", "requested_count:INTEGER:0::0",
            "models_requested:INTEGER:1::0", "models_succeeded_propose:INTEGER:1::0",
            "models_succeeded_critique:INTEGER:1::0", "reduced_confidence:INTEGER:1:0:0",
            "candidates_count:INTEGER:1::0", "dropped_count:INTEGER:1:0:0", "candidates_sha256:TEXT:1::0",
            "created_at:INTEGER:1::0", "imported_at:INTEGER:1::0"],
            await ReadStringsAsync(connection, ColumnsSql, "council_runs", CancellationToken.None));

        Assert.Equal([
            "word_id:INTEGER:1::1", "word_version:INTEGER:1::2", "council_run_id:INTEGER:1::3",
            "model_id:TEXT:1::4", "role:TEXT:1::5", "issues:TEXT:0::0"],
            await ReadStringsAsync(connection, ColumnsSql, "word_provenance", CancellationToken.None));

        Assert.Equal([
            "id:INTEGER:1::1", "council_run_id:INTEGER:1::0", "model_id:TEXT:1::0", "phase:TEXT:1::0",
            "status:TEXT:1::0", "latency_ms:INTEGER:0::0", "input_tokens:INTEGER:0::0",
            "output_tokens:INTEGER:0::0", "cost_usd_est:REAL:0::0", "invalid_count:INTEGER:1:0:0",
            "error:TEXT:0::0"],
            await ReadStringsAsync(connection, ColumnsSql, "council_model_results", CancellationToken.None));

        // CHECKs and the surrogate keys, exactly as §2.2.
        string runsSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "council_runs", CancellationToken.None));
        Assert.Contains("CHECK (mode IN ('propose', 'rework'))", runsSql);
        Assert.Contains("\"id\" INTEGER NOT NULL CONSTRAINT \"PK_council_runs\" PRIMARY KEY AUTOINCREMENT", runsSql);
        string resultsSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "council_model_results", CancellationToken.None));
        Assert.Contains("CHECK (phase IN ('propose', 'critique', 'topup'))", resultsSql);
        Assert.Contains("CHECK (status IN ('ok', 'failed', 'timeout', 'invalid'))", resultsSql);
        string provenanceSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "word_provenance", CancellationToken.None));
        Assert.Contains("CHECK (role IN ('proposed', 'critique_ok', 'critique_issue'))", provenanceSql);
        Assert.Contains("\"role\" TEXT NOT NULL,", provenanceSql);
        // The PK is composite and leads with word_id: asserted exactly above via pragma_table_info.

        // Indexes: unique run id, unique per-run model+phase, per-run provenance reads.
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_council_runs_run_id\" ON \"council_runs\" (\"run_id\")"],
            await ReadStringsAsync(connection, IndexSql, "IX_council_runs_run_id", CancellationToken.None));
        Assert.Equal(["CREATE INDEX \"ix_council_runs_created_at\" ON \"council_runs\" (\"created_at\")"],
            await ReadStringsAsync(connection, IndexSql, "ix_council_runs_created_at", CancellationToken.None));
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_council_model_results_council_run_id_model_id_phase\" ON \"council_model_results\" (\"council_run_id\", \"model_id\", \"phase\")"],
            await ReadStringsAsync(connection, IndexSql, "IX_council_model_results_council_run_id_model_id_phase", CancellationToken.None));
        Assert.Equal(["CREATE INDEX \"ix_word_provenance_run\" ON \"word_provenance\" (\"council_run_id\")"],
            await ReadStringsAsync(connection, IndexSql, "ix_word_provenance_run", CancellationToken.None));
        Assert.Equal(["CREATE INDEX \"ix_words_council_run_id\" ON \"words\" (\"council_run_id\")"],
            await ReadStringsAsync(connection, IndexSql, "ix_words_council_run_id", CancellationToken.None));

        // word_reviews: index only, deliberately no FK. words.council_run_id does carry REFERENCES.
        Assert.Equal(["CREATE INDEX \"ix_word_reviews_council_run_id\" ON \"word_reviews\" (\"council_run_id\")"],
            await ReadStringsAsync(connection, IndexSql, "ix_word_reviews_council_run_id", CancellationToken.None));
        Assert.All(await ReadStringsAsync(connection, ForeignKeysSql, "word_reviews", CancellationToken.None),
            foreignKey => Assert.DoesNotContain("council_runs", foreignKey));
        Assert.Contains("council_run_id:council_runs:id:NO ACTION",
            await ReadStringsAsync(connection, ForeignKeysSql, "words", CancellationToken.None));

        Assert.Equal(["council_run_id:council_runs:id:CASCADE"],
            await ReadStringsAsync(connection, ForeignKeysSql, "council_model_results", CancellationToken.None));
        Assert.Equal(["council_run_id:council_runs:id:NO ACTION", "word_id:words:id:CASCADE"],
            (await ReadStringsAsync(connection, ForeignKeysSql, "word_provenance", CancellationToken.None)).Order().ToArray());

        await AssertConstraintsAsync(database);
    }

    private static async Task AssertConstraintsAsync(PalabrasDbContext database)
    {
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,source,council_run_id,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','pending_review','hash','council','council',NULL,1,1);", CancellationToken.None);
        const string insertRun =
            "INSERT INTO council_runs (id,run_id,mode,prompt_versions,style_guide_sha256,models_requested,models_succeeded_propose," +
            "models_succeeded_critique,candidates_count,candidates_sha256,created_at,imported_at) " +
            "VALUES (1,'01JBRUN','propose','{{}}','sha',4,3,3,10,'sha',1,1);";
        await database.Database.ExecuteSqlRawAsync(insertRun, CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("UPDATE words SET council_run_id=1;", CancellationToken.None);

        // Unique run id: a bundle cannot be imported twice under one key.
        await AssertConstraintAsync(database,
            "INSERT INTO council_runs (run_id,mode,prompt_versions,style_guide_sha256,models_requested,models_succeeded_propose," +
            "models_succeeded_critique,candidates_count,candidates_sha256,created_at,imported_at) " +
            "VALUES ('01JBRUN','rework','{{}}','sha',4,3,3,10,'sha',1,1);");
        await AssertConstraintAsync(database, "UPDATE council_runs SET mode='bogus';");
        await database.Database.ExecuteSqlRawAsync("UPDATE council_runs SET mode='rework';", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("UPDATE council_runs SET mode='propose';", CancellationToken.None);

        // One outcome per model per phase.
        const string insertResult =
            "INSERT INTO council_model_results (council_run_id,model_id,phase,status) VALUES (1,'m1','propose','ok');";
        await database.Database.ExecuteSqlRawAsync(insertResult, CancellationToken.None);
        await AssertConstraintAsync(database, insertResult);
        await AssertConstraintAsync(database, "UPDATE council_model_results SET phase='aftercare';");
        await AssertConstraintAsync(database, "UPDATE council_model_results SET status='maybe';");
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO council_model_results (council_run_id,model_id,phase,status) VALUES (1,'m2','critique','failed');", CancellationToken.None);
        Assert.Equal(2, await database.CouncilModelResults.CountAsync(CancellationToken.None));

        // Provenance: composite key, role enum, and a run cannot vanish under a provenance row.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_provenance (word_id,word_version,council_run_id,model_id,role) VALUES (1,1,1,'m1','proposed');", CancellationToken.None);
        await AssertConstraintAsync(database,
            "INSERT INTO word_provenance (word_id,word_version,council_run_id,model_id,role) VALUES (1,1,1,'m1','proposed');");
        await AssertConstraintAsync(database,
            "INSERT INTO word_provenance (word_id,word_version,council_run_id,model_id,role) VALUES (1,1,1,'m2','bogus');");
        await AssertConstraintAsync(database, "DELETE FROM council_runs;");
        // Provenance is NO ACTION, so the run stays until its provenance rows are gone; the word's own
        // reference keeps it alive too. Model results are CASCADE.
        await database.Database.ExecuteSqlRawAsync("DELETE FROM word_provenance;", CancellationToken.None);
        await AssertConstraintAsync(database, "DELETE FROM council_runs;");
        await database.Database.ExecuteSqlRawAsync("UPDATE words SET council_run_id=NULL;", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("DELETE FROM council_runs;", CancellationToken.None);
        Assert.Empty(await database.CouncilModelResults.ToListAsync(CancellationToken.None));

        // The legacy alternatives still accept their old shape, and the new enums reject nonsense.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id,kind,value,reason,rank) VALUES (1,'synonym','tomar','context',1);", CancellationToken.None);
        await AssertConstraintAsync(database, "UPDATE word_alternatives SET field='aftercare';");
        await AssertConstraintAsync(database, "UPDATE word_alternatives SET region='MX';");
        await AssertConstraintAsync(database, "UPDATE word_alternatives SET register='slang';");
        await AssertConstraintAsync(database, "UPDATE word_alternatives SET source='bundle';");
        await AssertConstraintAsync(database, "UPDATE words SET source='bundle';");
        foreach (string source in new[] { "council", "manual", "reviewer" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE words SET source={source};", CancellationToken.None);
        }
        foreach (string source in new[] { "council", "reviewer", "manual", "previous" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_alternatives SET source={source};", CancellationToken.None);
        }
        foreach (string field in new[] { "lemma", "gloss", "example", "regional_note", "regional_variant", "register_variant" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_alternatives SET field={field};", CancellationToken.None);
        }
        foreach (string region in new[] { "ES", "ES-NA" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_alternatives SET region={region};", CancellationToken.None);
        }
        foreach (string register in new[] { "neutral", "colloquial", "formal" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE word_alternatives SET register={register};", CancellationToken.None);
        }
    }

    private const string ColumnsSql =
        "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name);";
    private const string TableSql = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
    private const string IndexSql = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";

    // "from:table:to:on_delete" — the 3rd column of pragma_foreign_key_list is the referenced column,
    // except for the composite-PK case, which these tables do not use.
    private const string ForeignKeysSql =
        "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);";

    private static async Task SeedAsync(PalabrasDbContext database)
    {
        // Raw SQL, not entities: the context model already has the CouncilImport columns while this
        // database is still at ReviewGate.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (2,'reviewer','reviewer','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id,kind,value,reason,rank) VALUES (1,'synonym','tomar','context',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_revisions (word_id,version,action,snapshot_json,created_at) VALUES (1,1,'approve','{{}}',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_reviews (word_id,word_version,reviewer_id,action,client_review_id,created_at) VALUES (1,1,2,'approve','review-1',1);", CancellationToken.None);
    }

    private static async Task<string> SchemaAsync(DbConnection connection) =>
        string.Join('\n', await ReadStringsAsync(connection,
            "SELECT type || ':' || name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name;", "", CancellationToken.None));

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
}
