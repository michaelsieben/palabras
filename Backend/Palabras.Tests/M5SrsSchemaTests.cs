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

public sealed class M5SrsSchemaTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesToFullSrsSchemaAtStartupAsync()
    {
        using var factory = new SrsApplicationFactory();
        Assert.False(File.Exists(factory.DatabasePath));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = new DatabaseSettings(factory.DatabasePath).CreateContext();
        Assert.Equal(4, (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count());
        Assert.Contains(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None), name => name.EndsWith("_SrsCards", StringComparison.Ordinal));
        Assert.Contains(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None), name => name.EndsWith("_DayStatusAndSkips", StringComparison.Ordinal));
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        DbConnection connection = database.Database.GetDbConnection();
        var expected = new Dictionary<string, string[]>
        {
            ["cards"] = [
                "id:INTEGER:1::1", "user_id:INTEGER:1::0", "sense_id:INTEGER:1::0", "direction:TEXT:1::0",
                "epoch:INTEGER:1:0:0", "state:INTEGER:1::0", "step:INTEGER:0::0", "stability:REAL:0::0",
                "difficulty:REAL:0::0", "due_ms:INTEGER:1::0", "last_review_ms:INTEGER:0::0",
                "reps:INTEGER:1:0:0", "lapses:INTEGER:1:0:0", "suspended_reason:TEXT:0::0",
                "buried_until_local_date:TEXT:0::0", "is_leech:INTEGER:1:0:0", "introduced_local_date:TEXT:0::0", "created_at:INTEGER:1::0"],
            ["user_settings"] = [
                "user_id:INTEGER:1::1", "desired_retention:REAL:1:0.90000000000000002:0", "new_per_day:INTEGER:1:12:0",
                "max_reviews_per_day:INTEGER:1:200:0", "backlog_threshold:INTEGER:1:150:0", "session_target_s:INTEGER:0:600:0",
                "session_max_s:INTEGER:0:900:0", "max_interval_days:INTEGER:1:730:0", "enable_fuzz:INTEGER:1:1:0",
                "learning_steps:TEXT:0:'[\"00:01:00\",\"00:10:00\"]':0", "relearning_steps:TEXT:0:'[\"00:10:00\"]':0",
                "leech_lapses:INTEGER:1:8:0", "weights_json:TEXT:0::0", "weights_version:INTEGER:1:0:0"],
            ["review_log"] = [
                "id:INTEGER:1::1", "client_attempt_id:TEXT:1::0", "user_id:INTEGER:1::0", "word_id:INTEGER:1::0",
                "card_id:INTEGER:1::0", "card_epoch:INTEGER:1::0", "session_id:TEXT:0::0", "rating:INTEGER:1::0",
                "reviewed_at_ms:INTEGER:1::0", "received_at_ms:INTEGER:1::0", "elapsed_days:REAL:1::0",
                "interval_before_days:REAL:0::0", "interval_after_days:REAL:1::0", "duration_ms:INTEGER:0::0", "mode:TEXT:1::0",
                "is_correct_meaning:INTEGER:0::0", "is_correct_form:INTEGER:0::0", "feedback_kind:TEXT:0::0",
                "overridden:INTEGER:1:0:0", "answer_text:TEXT:0::0", "state_before:INTEGER:1::0",
                "stability_before:REAL:0::0", "difficulty_before:REAL:0::0", "state_after:INTEGER:1::0",
                "stability_after:REAL:0::0", "difficulty_after:REAL:0::0", "due_after_ms:INTEGER:1::0",
                "weights_version:INTEGER:1::0", "counts_for_srs:INTEGER:0:1:0"]
        };
        foreach (var table in expected)
        {
            Assert.Equal(table.Value, await ReadAsync(connection,
                "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name) ORDER BY cid;", table.Key));
        }
        Assert.Contains("timezone:TEXT:0::0", await ReadAsync(connection,
            "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name);", "users"));
        string cardsSql = Assert.Single(await ReadAsync(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "cards"));
        Assert.Contains("CHECK (direction IN ('es_en', 'en_es'))", cardsSql);
        Assert.Contains("CHECK (state IN (0, 1, 2, 3))", cardsSql);
        Assert.Contains("CHECK (suspended_reason IN ('word_not_approved', 'sense_removed', 'manual', 'leech'))", cardsSql);
        string logSql = Assert.Single(await ReadAsync(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;", "review_log"));
        Assert.Contains("CHECK (rating BETWEEN 1 AND 4)", logSql);
        Assert.Contains("CHECK (mode IN ('recognition', 'typed', 'cloze'))", logSql);
        var indexes = new Dictionary<string, string>
        {
            ["ix_cards_due"] = "CREATE INDEX \"ix_cards_due\" ON \"cards\" (\"user_id\", \"due_ms\") WHERE suspended_reason IS NULL AND state <> 0",
            ["ix_cards_new"] = "CREATE INDEX \"ix_cards_new\" ON \"cards\" (\"user_id\", \"created_at\") WHERE suspended_reason IS NULL AND state = 0",
            ["IX_cards_sense_id_user_id_direction"] = "CREATE UNIQUE INDEX \"IX_cards_sense_id_user_id_direction\" ON \"cards\" (\"sense_id\", \"user_id\", \"direction\")",
            ["IX_review_log_client_attempt_id"] = "CREATE UNIQUE INDEX \"IX_review_log_client_attempt_id\" ON \"review_log\" (\"client_attempt_id\")",
            ["IX_review_log_card_id_reviewed_at_ms"] = "CREATE INDEX \"IX_review_log_card_id_reviewed_at_ms\" ON \"review_log\" (\"card_id\", \"reviewed_at_ms\")",
            ["IX_review_log_user_id_reviewed_at_ms"] = "CREATE INDEX \"IX_review_log_user_id_reviewed_at_ms\" ON \"review_log\" (\"user_id\", \"reviewed_at_ms\")"
        };
        foreach (var index in indexes)
        {
            Assert.Equal(index.Value, Assert.Single(await ReadAsync(connection, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;", index.Key)));
        }
        Assert.Equal(["sense_id:senses:id:CASCADE", "user_id:users:id:NO ACTION"], (await ForeignKeysAsync(connection, "cards")).Order().ToArray());
        Assert.Equal(["user_id:users:id:NO ACTION"], await ForeignKeysAsync(connection, "user_settings"));
        Assert.Equal(["card_id:cards:id:NO ACTION", "user_id:users:id:NO ACTION", "word_id:words:id:NO ACTION"], (await ForeignKeysAsync(connection, "review_log")).Order().ToArray());
    }

    [Fact]
    public async Task UpgradePreservesExistingContentAndAccountsAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("ContentSchema", CancellationToken.None);
        await SeedAsync(database);
        DbConnection connection = database.Database.GetDbConnection();
        string[] before = await ReadAsync(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name IN ('words','senses','word_alternatives','word_topics','word_revisions') ORDER BY name;", "");
        await migrator.MigrateAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(before, await ReadAsync(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name IN ('words','senses','word_alternatives','word_topics','word_revisions') ORDER BY name;", ""));
        Assert.Equal("owner", (await database.Users.AsNoTracking().SingleAsync(CancellationToken.None)).Username);
        Assert.Null((await database.Users.AsNoTracking().SingleAsync(CancellationToken.None)).Timezone);
        Assert.Equal("es-coger-01", (await database.Words.SingleAsync(CancellationToken.None)).Key);
        Assert.Single(await database.Senses.ToListAsync(CancellationToken.None));
        string script = migrator.GenerateScript("InitialAccounts", "SrsCards");
        Assert.DoesNotContain("DROP", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "ALTER TABLE", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("ALTER TABLE \"users\" ADD \"timezone\" TEXT NULL;", script);
    }

    [Fact]
    public async Task DefaultsChecksUniqueKeysAndForeignKeysAreEnforcedAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        await SeedAsync(database);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO user_settings (user_id) VALUES (1);", CancellationToken.None);
        UserSettings settings = await database.UserSettings.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(0.90, settings.DesiredRetention);
        Assert.Equal(12, settings.NewPerDay);
        Assert.Equal(200, settings.MaxReviewsPerDay);
        Assert.Equal(150, settings.BacklogThreshold);
        Assert.Equal(600, settings.SessionTargetS);
        Assert.Equal(900, settings.SessionMaxS);
        Assert.Equal(730, settings.MaxIntervalDays);
        Assert.True(settings.EnableFuzz);
        Assert.Equal("[\"00:01:00\",\"00:10:00\"]", settings.LearningSteps);
        Assert.Equal("[\"00:10:00\"]", settings.RelearningSteps);
        Assert.Equal(8, settings.LeechLapses);
        Assert.Null(settings.WeightsJson);
        Assert.Equal(0, settings.WeightsVersion);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,1792886400123,1792886400123);", CancellationToken.None);
        Card card = await database.Cards.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(1792886400123L, card.DueMs);
        Assert.Equal(0, card.Epoch);
        Assert.Equal(0, card.Reps);
        Assert.Equal(0, card.Lapses);
        Assert.False(card.IsLeech);
        Assert.Null(card.LastReviewMs);
        foreach (string sql in new[] {
            "UPDATE cards SET direction='invalid';", "UPDATE cards SET state=-1;", "UPDATE cards SET state=4;",
            "UPDATE cards SET suspended_reason='invalid';", "UPDATE cards SET user_id=999;", "UPDATE cards SET sense_id=999;",
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,0);",
            "INSERT INTO user_settings (user_id) VALUES (1);", "INSERT INTO user_settings (user_id) VALUES (999);" })
        {
            await ConstraintFailsAsync(database, sql);
        }
        foreach (string reason in new[] { "word_not_approved", "sense_removed", "manual", "leech" })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE cards SET suspended_reason={reason};", CancellationToken.None);
        }
        await database.Database.ExecuteSqlRawAsync("UPDATE cards SET suspended_reason=NULL;", CancellationToken.None);
        const string insertLog = "INSERT INTO review_log (client_attempt_id,user_id,word_id,card_id,card_epoch,rating,reviewed_at_ms,received_at_ms,elapsed_days,interval_after_days,mode,state_before,state_after,due_after_ms,weights_version) VALUES ('attempt',1,1,1,0,3,1792886400123,1792886400456,0,1,'recognition',0,1,1792972800123,0);";
        await database.Database.ExecuteSqlRawAsync(insertLog, CancellationToken.None);
        ReviewLog log = await database.ReviewLogs.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.True(log.CountsForSrs);
        Assert.False(log.Overridden);
        Assert.Null(log.IntervalBeforeDays);
        Assert.Equal(1792886400123L, log.ReviewedAtMs);
        Assert.Equal(1792886400456L, log.ReceivedAtMs);
        await ConstraintFailsAsync(database, insertLog);
        foreach (string sql in new[] { "UPDATE review_log SET rating=0;", "UPDATE review_log SET rating=5;", "UPDATE review_log SET mode='invalid';", "UPDATE review_log SET card_id=999;", "UPDATE review_log SET user_id=999;", "UPDATE review_log SET word_id=999;" })
        {
            await ConstraintFailsAsync(database, sql);
        }
        // Explicit false/zero values must not be replaced by server defaults on EF updates.
        settings = await database.UserSettings.SingleAsync(CancellationToken.None);
        settings.EnableFuzz = false;
        settings.NewPerDay = 0;
        await database.SaveChangesAsync(CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.False((await database.UserSettings.SingleAsync(CancellationToken.None)).EnableFuzz);
        Assert.Equal(0, (await database.UserSettings.SingleAsync(CancellationToken.None)).NewPerDay);
        await database.UserSettings.ExecuteDeleteAsync(CancellationToken.None);
        database.ChangeTracker.Clear();
        database.UserSettings.Add(new UserSettings { UserId = 1, EnableFuzz = false, NewPerDay = 0, MaxReviewsPerDay = 0, BacklogThreshold = 0 });
        database.ReviewLogs.Add(new ReviewLog { UserId = 1, WordId = 1, CardId = 1, ClientAttemptId = "not-applied", Rating = Palabras.Srs.Rating.Good, CountsForSrs = false });
        await database.SaveChangesAsync(CancellationToken.None);
        database.ChangeTracker.Clear();
        UserSettings inserted = await database.UserSettings.SingleAsync(CancellationToken.None);
        Assert.False(inserted.EnableFuzz);
        Assert.Equal(0, inserted.NewPerDay);
        Assert.Equal(0, inserted.MaxReviewsPerDay);
        Assert.Equal(0, inserted.BacklogThreshold);
        Assert.False((await database.ReviewLogs.SingleAsync(value => value.ClientAttemptId == "not-applied", CancellationToken.None)).CountsForSrs);
        await database.ReviewLogs.ExecuteDeleteAsync(CancellationToken.None);
        await database.Senses.ExecuteDeleteAsync(CancellationToken.None);
        Assert.Empty(await database.Cards.ToListAsync(CancellationToken.None));
    }

    private static async Task SeedAsync(PalabrasDbContext database)
    {
        // Raw SQL permits seeding the pre-SrsCards users table during the additive-upgrade test.
        await database.Database.ExecuteSqlRawAsync("INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash','2026-10-18','2026-10-18');", CancellationToken.None);
        database.Words.Add(new Word { Id = 1, Key = "es-coger-01", Headword = "coger", Pos = "verb", Cefr = "A1", RegionScope = "ES", Register = "neutral", ContentHash = "hash", ProvenanceMethod = "manual" });
        database.Senses.Add(new Sense { Id = 1, WordId = 1, SenseOrder = 1, EnGloss = "take", ExampleEs = "Lo cojo.", ExampleEn = "I take it." });
        await database.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task ConstraintFailsAsync(PalabrasDbContext database, string sql)
    {
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync(sql, CancellationToken.None));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    private static Task<string[]> ForeignKeysAsync(DbConnection connection, string table) => ReadAsync(connection,
        "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);", table);

    private static async Task<string[]> ReadAsync(DbConnection connection, string sql, string name)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        var values = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }

    private sealed class SrsApplicationFactory : WebApplicationFactory<Program>
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
