using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Palabras.Persistence;
using Palabras.Tests.Srs;
using Xunit;

namespace Palabras.Tests;

// M8 reminder/Web Push schema (plan §2.3, design-amendment.md ΔC.2/ΔF). The upgrade test is the one
// that matters: the migration must build on a live database at WordDifficulty by creating five new
// tables only. Any statement that rebuilds an existing table would open the crash window documented
// in docs/08-upgrade-rollback.md §G, which stays ReviewGate-only.
public sealed class M8PushSchemaTests
{
    private static readonly string[] NewTables =
        ["notification_actions", "push_deliveries", "push_subscriptions", "reminder_log", "reminder_settings"];

    private static readonly string[] NewIndexes =
    [
        "IX_notification_actions_reminder_log_id", "IX_notification_actions_user_id",
        "IX_push_deliveries_reminder_log_id_subscription_id", "IX_push_deliveries_subscription_id",
        "IX_push_subscriptions_endpoint", "ix_push_subscriptions_user",
        "IX_reminder_log_user_id_local_date_kind_channel"
    ];

    [Fact]
    public async Task UpgradeFromWordDifficultyCreatesOnlyNewTablesAndIsRerunSafeAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("WordDifficulty", CancellationToken.None);
        await SeedAsync(database);
        DbConnection connection = database.Database.GetDbConnection();
        Dictionary<string, string> schemaBefore = await SchemaAsync(connection);
        string[] rowsBefore = await PlainRowsAsync(connection);

        await migrator.MigrateAsync(cancellationToken: CancellationToken.None);

        // Additive only: every table and index that existed before the migration is byte-identical
        // afterwards — no rebuild, no renamed copy left behind, no recreated index.
        Dictionary<string, string> schemaAfter = await SchemaAsync(connection);
        foreach (KeyValuePair<string, string> entry in schemaBefore)
        {
            Assert.True(schemaAfter.TryGetValue(entry.Key, out string? definition), entry.Key);
            Assert.Equal(entry.Value, definition);
        }
        Assert.Equal(NewTables.Concat(NewIndexes).Order(StringComparer.Ordinal).ToArray(),
            schemaAfter.Keys.Except(schemaBefore.Keys).Order(StringComparer.Ordinal).ToArray());

        // The seeded pre-migration rows survive unchanged.
        Assert.Equal(rowsBefore, await PlainRowsAsync(connection));

        // A second apply is a no-op: same migration history, nothing pending, same schema.
        string schema = string.Join('\n', schemaAfter.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key + ":" + entry.Value));
        int applied = (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count();
        Assert.Equal(9, applied);
        await database.Database.MigrateAsync(CancellationToken.None);
        Assert.Equal(applied, (await database.Database.GetAppliedMigrationsAsync(CancellationToken.None)).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(CancellationToken.None));
        Assert.Equal(schema, string.Join('\n', (await SchemaAsync(connection)).OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key + ":" + entry.Value)));
        Assert.Empty(await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name GLOB 'ef_temp_*';", "", CancellationToken.None));

        // No statement touches an existing table: the script is CREATE TABLE / CREATE INDEX only.
        string script = migrator.GenerateScript("WordDifficulty", "PushReminders");
        Assert.DoesNotContain("ef_temp_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
        string[] statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // One transaction, no suppressed statement: the whole migration commits or rolls back as a unit.
        Assert.Equal(1, statements.Count(statement => statement == "BEGIN TRANSACTION"));
        Assert.Equal(1, statements.Count(statement => statement == "COMMIT"));
        foreach (string statement in statements.Where(statement => statement is not ("BEGIN TRANSACTION" or "COMMIT")))
        {
            Assert.True(
                statement.StartsWith("CREATE TABLE", StringComparison.Ordinal)
                || statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                || statement.StartsWith("CREATE INDEX", StringComparison.Ordinal)
                || statement.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal),
                statement);
        }
        // The only non-DDL statement is the history insert that records the migration as applied.
        Assert.Single(statements, statement => !statement.StartsWith("CREATE ", StringComparison.Ordinal)
            && statement is not ("BEGIN TRANSACTION" or "COMMIT"));
        Assert.Equal(5, script.Split("CREATE TABLE", StringSplitOptions.None).Length - 1);
        Assert.Equal(7, script.Split("CREATE INDEX", StringSplitOptions.None).Length - 1
            + script.Split("CREATE UNIQUE INDEX", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task NewTablesHaveTheDocumentedShapeAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        DbConnection connection = database.Database.GetDbConnection();

        // name:type:not-null:default:primary-key-order, from the migrated database.
        Assert.Equal([
            "id:INTEGER:1::1", "user_id:INTEGER:1::0", "endpoint:TEXT:1::0", "p256dh:TEXT:1::0",
            "auth:TEXT:1::0", "vapid_key_id:TEXT:1::0", "device_label:TEXT:0::0", "state:TEXT:1:'active':0",
            "created_at_ms:INTEGER:1::0", "updated_at_ms:INTEGER:1::0", "last_success_at_ms:INTEGER:0::0",
            "last_failure_at_ms:INTEGER:0::0", "failure_count:INTEGER:1:0:0", "last_status:INTEGER:0::0"],
            await ReadStringsAsync(connection, ColumnsSql, "push_subscriptions", CancellationToken.None));

        Assert.Equal([
            "user_id:INTEGER:1::1", "enabled:INTEGER:1:1:0", "reminder_time:TEXT:1:'22:00':0",
            "muted_until:TEXT:0::0", "send_window_min:INTEGER:1:90:0", "updated_at_ms:INTEGER:1::0"],
            await ReadStringsAsync(connection, ColumnsSql, "reminder_settings", CancellationToken.None));

        Assert.Equal([
            "id:INTEGER:1::1", "user_id:INTEGER:1::0", "local_date:TEXT:1::0", "kind:TEXT:1::0",
            "channel:TEXT:1::0", "result:TEXT:1::0", "ttl_s:INTEGER:0::0", "expires_at_ms:INTEGER:0::0",
            "skip_available:INTEGER:1::0", "created_at_ms:INTEGER:1::0", "updated_at_ms:INTEGER:1::0"],
            await ReadStringsAsync(connection, ColumnsSql, "reminder_log", CancellationToken.None));

        Assert.Equal([
            "id:INTEGER:1::1", "reminder_log_id:INTEGER:1::0", "subscription_id:INTEGER:0::0",
            "attempts:INTEGER:1:0:0", "result:TEXT:1::0", "last_status:INTEGER:0::0", "updated_at_ms:INTEGER:1::0"],
            await ReadStringsAsync(connection, ColumnsSql, "push_deliveries", CancellationToken.None));

        Assert.Equal([
            "id:INTEGER:1::1", "user_id:INTEGER:1::0", "reminder_log_id:INTEGER:0::0", "local_date:TEXT:1::0",
            "action:TEXT:1::0", "result:TEXT:1::0", "mute_choice:TEXT:0::0", "created_at_ms:INTEGER:1::0"],
            await ReadStringsAsync(connection, ColumnsSql, "notification_actions", CancellationToken.None));

        // CHECKs, exactly as plan §2.3. They are legal because every table here is new.
        string subscriptionsSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "push_subscriptions", CancellationToken.None));
        Assert.Contains("CHECK (state IN ('active', 'stale_key', 'rejected'))", subscriptionsSql);
        Assert.Contains("\"endpoint\" TEXT NOT NULL,", subscriptionsSql);
        string settingsSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "reminder_settings", CancellationToken.None));
        Assert.Contains("CHECK (length(reminder_time) = 5)", settingsSql);
        Assert.Contains("CHECK (send_window_min BETWEEN 15 AND 240)", settingsSql);
        string logSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "reminder_log", CancellationToken.None));
        Assert.Contains("CHECK (kind IN ('daily'))", logSql);
        Assert.Contains("CHECK (channel IN ('webpush'))", logSql);
        Assert.Contains("CHECK (result IN ('sending', 'sent', 'failed', 'gone', 'window_missed', 'no_target'))", logSql);
        string deliveriesSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "push_deliveries", CancellationToken.None));
        Assert.Contains("CHECK (result IN ('pending', 'sent', 'gone', 'rejected', 'transient'))", deliveriesSql);
        string actionsSql = Assert.Single(await ReadStringsAsync(connection, TableSql, "notification_actions", CancellationToken.None));
        Assert.Contains("CHECK (action IN ('open', 'skip', 'mute'))", actionsSql);
        Assert.Contains("CHECK (result IN ('ok', 'skip_unavailable', 'already_done', 'unauthenticated', 'error'))", actionsSql);
        Assert.Contains("CHECK (mute_choice IN ('tomorrow', '3d', '7d', 'disable', 'time_changed', 'cancel'))", actionsSql);

        // Indexes: one deliverable-device lookup, a unique endpoint, one decision per learner-day, one
        // attempt row per device per decision.
        Assert.Equal(["CREATE INDEX \"ix_push_subscriptions_user\" ON \"push_subscriptions\" (\"user_id\", \"state\")"],
            await ReadStringsAsync(connection, IndexSql, "ix_push_subscriptions_user", CancellationToken.None));
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_push_subscriptions_endpoint\" ON \"push_subscriptions\" (\"endpoint\")"],
            await ReadStringsAsync(connection, IndexSql, "IX_push_subscriptions_endpoint", CancellationToken.None));
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_reminder_log_user_id_local_date_kind_channel\" ON \"reminder_log\" (\"user_id\", \"local_date\", \"kind\", \"channel\")"],
            await ReadStringsAsync(connection, IndexSql, "IX_reminder_log_user_id_local_date_kind_channel", CancellationToken.None));
        Assert.Equal(["CREATE UNIQUE INDEX \"IX_push_deliveries_reminder_log_id_subscription_id\" ON \"push_deliveries\" (\"reminder_log_id\", \"subscription_id\")"],
            await ReadStringsAsync(connection, IndexSql, "IX_push_deliveries_reminder_log_id_subscription_id", CancellationToken.None));

        Assert.Equal(["user_id:users:id:NO ACTION"],
            await ReadStringsAsync(connection, ForeignKeysSql, "push_subscriptions", CancellationToken.None));
        Assert.Equal(["user_id:users:id:NO ACTION"],
            await ReadStringsAsync(connection, ForeignKeysSql, "reminder_settings", CancellationToken.None));
        Assert.Equal(["user_id:users:id:NO ACTION"],
            await ReadStringsAsync(connection, ForeignKeysSql, "reminder_log", CancellationToken.None));
        Assert.Equal(["reminder_log_id:reminder_log:id:CASCADE", "subscription_id:push_subscriptions:id:SET NULL"],
            (await ReadStringsAsync(connection, ForeignKeysSql, "push_deliveries", CancellationToken.None)).Order().ToArray());
        Assert.Equal(["reminder_log_id:reminder_log:id:SET NULL", "user_id:users:id:NO ACTION"],
            (await ReadStringsAsync(connection, ForeignKeysSql, "notification_actions", CancellationToken.None)).Order().ToArray());
    }

    [Fact]
    public async Task ConstraintsUniquesAndReferencesAreEnforcedAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);

        // push_subscriptions: defaults, one row per endpoint, enum state, FK.
        const string insertSubscription =
            "INSERT INTO push_subscriptions (user_id,endpoint,p256dh,auth,vapid_key_id,device_label,created_at_ms,updated_at_ms) " +
            "VALUES (1,'https://fcm.googleapis.com/fcm/send/a','p','a','0123456789abcdef','OnePlus 15',1,1);";
        await database.Database.ExecuteSqlRawAsync(insertSubscription, CancellationToken.None);
        PushSubscription subscription = await database.PushSubscriptions.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("active", subscription.State);
        Assert.Equal(0, subscription.FailureCount);
        Assert.Null(subscription.LastStatus);
        await AssertConstraintAsync(database, insertSubscription);
        await AssertConstraintAsync(database, "UPDATE push_subscriptions SET state='bogus';");
        await AssertConstraintAsync(database,
            "INSERT INTO push_subscriptions (user_id,endpoint,p256dh,auth,vapid_key_id,created_at_ms,updated_at_ms) " +
            "VALUES (999,'https://fcm.googleapis.com/fcm/send/b','p','a','k',1,1);");

        // reminder_settings: exactly the documented defaults when only the owner key is supplied.
        await database.Database.ExecuteSqlRawAsync("INSERT INTO reminder_settings (user_id,updated_at_ms) VALUES (1,1);", CancellationToken.None);
        ReminderSettings settings = await database.ReminderSettings.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.True(settings.Enabled);
        Assert.Equal("22:00", settings.ReminderTime);
        Assert.Null(settings.MutedUntil);
        Assert.Equal(90, settings.SendWindowMin);
        await AssertConstraintAsync(database, "INSERT INTO reminder_settings (user_id,updated_at_ms) VALUES (1,2);");
        await AssertConstraintAsync(database, "UPDATE reminder_settings SET reminder_time='2:00';");
        await AssertConstraintAsync(database, "UPDATE reminder_settings SET send_window_min=14;");
        await AssertConstraintAsync(database, "UPDATE reminder_settings SET send_window_min=241;");
        foreach (int window in new[] { 15, 240 })
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE reminder_settings SET send_window_min={window};", CancellationToken.None);
        }

        // reminder_log: the once-per-learner-day claim, and the three enum domains.
        const string insertLog =
            "INSERT INTO reminder_log (user_id,local_date,kind,channel,result,skip_available,created_at_ms,updated_at_ms) " +
            "VALUES (1,'2026-10-20','daily','webpush','sending',1,1,1);";
        await database.Database.ExecuteSqlRawAsync(insertLog, CancellationToken.None);
        ReminderLog log = await database.ReminderLogs.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Null(log.TtlS);
        Assert.Null(log.ExpiresAtMs);
        Assert.True(log.SkipAvailable);
        await AssertConstraintAsync(database, insertLog);
        // A different local date is a different decision.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO reminder_log (user_id,local_date,kind,channel,result,skip_available,created_at_ms,updated_at_ms) " +
            "VALUES (1,'2026-10-21','daily','webpush','sent',0,1,1);", CancellationToken.None);
        await AssertConstraintAsync(database, "UPDATE reminder_log SET kind='weekly';");
        await AssertConstraintAsync(database, "UPDATE reminder_log SET channel='email';");
        await AssertConstraintAsync(database, "UPDATE reminder_log SET result='maybe';");

        // push_deliveries: one attempt row per device per decision, and SET NULL when the device row
        // is deleted on 404/410 while the attempt history stays.
        const string insertDelivery =
            "INSERT INTO push_deliveries (reminder_log_id,subscription_id,result,updated_at_ms) VALUES (1,1,'pending',1);";
        await database.Database.ExecuteSqlRawAsync(insertDelivery, CancellationToken.None);
        PushDelivery delivery = await database.PushDeliveries.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal(0, delivery.Attempts);
        Assert.Null(delivery.LastStatus);
        await AssertConstraintAsync(database, insertDelivery);
        await AssertConstraintAsync(database, "UPDATE push_deliveries SET result='maybe';");
        await AssertConstraintAsync(database,
            "INSERT INTO push_deliveries (reminder_log_id,subscription_id,result,updated_at_ms) VALUES (999,1,'pending',1);");
        await database.Database.ExecuteSqlRawAsync("DELETE FROM push_subscriptions;", CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.Null((await database.PushDeliveries.AsNoTracking().SingleAsync(CancellationToken.None)).SubscriptionId);

        // notification_actions: audit rows, nullable mute choice, SET NULL when the decision is purged.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO notification_actions (user_id,reminder_log_id,local_date,action,result,mute_choice,created_at_ms) " +
            "VALUES (1,1,'2026-10-20','mute','ok','tomorrow',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO notification_actions (user_id,local_date,action,result,created_at_ms) VALUES (1,'2026-10-20','open','ok',1);", CancellationToken.None);
        Assert.Equal(2, await database.NotificationActions.CountAsync(CancellationToken.None));
        await AssertConstraintAsync(database, "UPDATE notification_actions SET action='dismiss';");
        await AssertConstraintAsync(database, "UPDATE notification_actions SET result='maybe';");
        await AssertConstraintAsync(database, "UPDATE notification_actions SET mute_choice='never';");
        // NULL passes the mute_choice CHECK, so a plain open action is valid.
        Assert.Single(await database.NotificationActions.AsNoTracking()
            .Where(action => action.MuteChoice == null).ToListAsync(CancellationToken.None));
        await database.Database.ExecuteSqlRawAsync("DELETE FROM reminder_log;", CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.All(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None),
            action => Assert.Null(action.ReminderLogId));
        Assert.Empty(await database.PushDeliveries.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LegacyInsertStatementsStillSucceedAsync()
    {
        await using var rig = new SrsTestDatabase();
        PalabrasDbContext database = rig.Context;
        await database.Database.MigrateAsync(CancellationToken.None);

        // The exact column lists an image built before this migration uses. A rollback keeps the new
        // database, so every one of them must still succeed: the migration adds no column anywhere.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (1,'owner','owner','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO practice_sessions (user_id,local_date,started_at_ms,new_count,review_count,status) " +
            "VALUES (1,'2026-10-20',1,0,0,'completed');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO day_status (user_id,local_date,status,decided_at_ms) VALUES (1,'2026-10-20','done',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO day_skips (user_id,local_date,source,created_at_ms) VALUES (1,'2026-10-19','app',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO user_settings (user_id) VALUES (1);", CancellationToken.None);

        database.ChangeTracker.Clear();
        Assert.Equal("es-coger-01", (await database.Words.AsNoTracking().SingleAsync(CancellationToken.None)).Key);
        Assert.Equal("done", (await database.DayStatuses.AsNoTracking().SingleAsync(CancellationToken.None)).Status);
        Assert.Equal(12, (await database.UserSettings.AsNoTracking().SingleAsync(CancellationToken.None)).NewPerDay);
        // And the previous image still writes nothing into the new tables: no rows, no rows needed.
        Assert.Empty(await database.PushSubscriptions.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.ReminderSettings.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.ReminderLogs.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.PushDeliveries.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await database.NotificationActions.AsNoTracking().ToListAsync(CancellationToken.None));
    }

    private const string ColumnsSql =
        "SELECT name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '') || ':' || pk FROM pragma_table_info($name) ORDER BY cid;";
    private const string TableSql = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
    private const string IndexSql = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";

    // "from:table:to:on_delete" — the 3rd column of pragma_foreign_key_list is the referenced column,
    // except for the composite-PK case, which these tables do not use.
    private const string ForeignKeysSql =
        "SELECT \"from\" || ':' || \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list($name);";

    private static async Task SeedAsync(PalabrasDbContext database)
    {
        // Raw SQL, not entities: the context model already has the M8 tables while this database is
        // still at WordDifficulty. The rows cover every pre-migration table a learner or an operator
        // can have data in, so "nothing changed" is asserted against real rows, not an empty schema.
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at,timezone) " +
            "VALUES (1,'owner','owner','hash',1,1,'Europe/Madrid');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id,username,role,password_hash,created_at,updated_at) VALUES (2,'reviewer','reviewer','hash',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO words (id,key,headword,pos,cefr,region_scope,register,status,content_hash,provenance_method,source,difficulty,created_at,updated_at) " +
            "VALUES (1,'es-coger-01','coger','verb','A1','ES','neutral','approved','hash','manual','manual',3,1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO senses (id,word_id,sense_order,en_gloss,example_es,example_en) VALUES (1,1,1,'take','Lo cojo.','I take it.');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_alternatives (word_id,kind,value,reason,rank) VALUES (1,'synonym','tomar','context',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_revisions (word_id,version,action,snapshot_json,created_at) VALUES (1,1,'approve','{{}}',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO word_reviews (word_id,word_version,reviewer_id,action,client_review_id,created_at) " +
            "VALUES (1,1,2,'approve','review-1',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO cards (user_id,sense_id,direction,state,due_ms,created_at) VALUES (1,1,'es_en',0,0,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO user_settings (user_id) VALUES (1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO practice_sessions (id,user_id,local_date,started_at_ms,completed_at_ms,new_count,review_count,status) " +
            "VALUES (1,1,'2026-10-20',1,2,0,0,'completed');", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO day_status (user_id,local_date,status,session_id,decided_at_ms) VALUES (1,'2026-10-20','done',1,1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            "INSERT INTO day_skips (user_id,local_date,source,created_at_ms) VALUES (1,'2026-10-19','notification',1);", CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO app_meta (key,value) VALUES ('rollover_cutover_ms','1');", CancellationToken.None);
    }

    // One string per seeded row family, in a fixed order: enough to prove the rows survived the
    // migration without depending on the M8 tables that do not exist yet.
    private static async Task<string[]> PlainRowsAsync(DbConnection connection) =>
    [
        .. await ReadStringsAsync(connection, "SELECT username || ':' || role || ':' || coalesce(timezone,'') FROM users ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT key || ':' || status || ':' || difficulty FROM words ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT en_gloss FROM senses ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT kind || ':' || value FROM word_alternatives ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT client_review_id FROM word_reviews ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT direction || ':' || state FROM cards ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT new_per_day FROM user_settings ORDER BY user_id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT local_date || ':' || status || ':' || session_id FROM day_status ORDER BY user_id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT local_date || ':' || source FROM day_skips ORDER BY id;", "", CancellationToken.None),
        .. await ReadStringsAsync(connection, "SELECT key || ':' || value FROM app_meta ORDER BY key;", "", CancellationToken.None)
    ];

    private static async Task<Dictionary<string, string>> SchemaAsync(DbConnection connection)
    {
        string[] rows = await ReadStringsAsync(connection,
            "SELECT name || ':' || type || ':' || coalesce(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;", "", CancellationToken.None);
        return rows.ToDictionary(row => row[..row.IndexOf(':', StringComparison.Ordinal)],
            row => row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);
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
