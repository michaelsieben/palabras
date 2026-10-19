using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Palabras.Persistence;
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

// U2: words.difficulty is the intra-band authoring rank. The migration must be the one additive
// shape SQLite supports for a nullable int — a plain ALTER TABLE … ADD COLUMN — because a rebuild of
// `words` would widen the crash window documented for ReviewGate in docs/08-upgrade-rollback.md §G.
// ReviewGate stays the only rebuild in this repo.
public sealed class M3WordDifficultySchemaTests
{
    [Fact]
    public async Task UpgradeFromCouncilImportAddsOnlyTheNullableColumnAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("CouncilImport", CancellationToken.None);
        await SeedAsync(database);
        DbConnection connection = database.Database.GetDbConnection();
        Dictionary<string, string> wordsBefore = await ColumnsAsync(connection, "words");
        string[] rowsBefore = await WordRowsAsync(connection);

        await migrator.MigrateAsync(cancellationToken: CancellationToken.None);

        // Additive only: every pre-existing column keeps its name, type, nullability and default.
        Dictionary<string, string> wordsAfter = await ColumnsAsync(connection, "words");
        foreach (KeyValuePair<string, string> column in wordsBefore)
        {
            Assert.True(wordsAfter.TryGetValue(column.Key, out string? definition), column.Key);
            Assert.Equal(column.Value, definition);
        }
        Assert.Equal(wordsBefore.Keys.Append("difficulty").Order().ToArray(), wordsAfter.Keys.Order().ToArray());
        Assert.Equal("INTEGER:0:", wordsAfter["difficulty"]);

        // Existing rows survive the ALTER and the new column is NULL on them: an old corpus is still
        // importable and still readable after the upgrade.
        Assert.Equal(rowsBefore, await WordRowsAsync(connection));
        Word word = await database.Words.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Null(word.Difficulty);
        Assert.Equal("hash", word.ContentHash);

        // A second apply is a no-op: same migration history, nothing pending, same schema.
        string schema = await SchemaAsync(connection);
        int applied = (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count();
        await database.Database.MigrateAsync(CancellationToken.None);
        Assert.Equal(applied, (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        Assert.Equal(schema, await SchemaAsync(connection));
    }

    [Fact]
    public async Task GeneratedScriptIsOneAlterWithoutARebuildAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();

        string script = migrator.GenerateScript("CouncilImport", "WordDifficulty");
        Assert.DoesNotContain("ef_temp_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA foreign_keys", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        string statement = Assert.Single(script.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()), part => part.StartsWith("ALTER TABLE", StringComparison.Ordinal));
        Assert.Equal("ALTER TABLE \"words\" ADD \"difficulty\" INTEGER NULL", statement);
    }

    [Fact]
    public async Task LegacyInsertStatementsStillSucceedAndLeaveTheRankNullAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);

        // The exact column lists an image built before this migration uses. A rollback keeps the new
        // database, so they must still succeed and take NULL for the added column.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);

        database.ChangeTracker.Clear();
        Assert.Null((await database.Words.AsNoTracking().SingleAsync(CancellationToken.None)).Difficulty);
    }

    private static Task<string[]> WordRowsAsync(DbConnection connection) => ReadStringsAsync(connection,
        "SELECT key || ':' || status || ':' || version || ':' || content_hash FROM words ORDER BY id;", "", CancellationToken.None);

    private static async Task<string> SchemaAsync(DbConnection connection) =>
        string.Join('\n', await ReadStringsAsync(connection,
            "SELECT type || ':' || name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name;", "", CancellationToken.None));

    private static async Task<Dictionary<string, string>> ColumnsAsync(DbConnection connection, string table)
    {
        string[] rows = await ReadStringsAsync(connection,
            "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') FROM pragma_table_info($name);", table, CancellationToken.None);
        return rows.ToDictionary(row => row[..row.IndexOf(':', StringComparison.Ordinal)], row => row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);
    }

    private static async Task SeedAsync(PalabrasDbContext database)
    {
        // Raw SQL, not entities: the context model already has the difficulty column while this
        // database is still at CouncilImport.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,1);", CancellationToken.None);
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
