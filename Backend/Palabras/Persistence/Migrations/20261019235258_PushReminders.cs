using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The 9th migration (M8 reminder + Web Push schema, plan §2.3). It is purely additive: five new
    /// tables plus their indexes, and <b>no</b> statement touches an existing table. That is what keeps
    /// the CHECK constraints legal — SQLite cannot add or change a CHECK on an existing table, and EF
    /// would answer with a full rebuild and a <c>PRAGMA foreign_keys = 0</c> window outside the
    /// migration transaction (docs/08-upgrade-rollback.md §G). ReviewGate remains the repo's only
    /// rebuild; every other migration is additive (§H).
    ///
    /// Reminder settings get their own table instead of new <c>user_settings</c> columns for the same
    /// reason: a column with a CHECK on an existing table forces a rebuild, while a new table stays
    /// additive and keeps its constraints.
    ///
    /// Do not replace these <c>CreateTable</c> calls with anything that rebuilds an existing table. The
    /// schema test (<c>M8PushSchemaTests</c>) fails if the generated script contains
    /// <c>ef_temp_</c>, <c>PRAGMA</c>, <c>DROP</c>, <c>RENAME</c> or <c>ALTER TABLE</c>.
    /// </summary>
    public partial class PushReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    p256dh = table.Column<string>(type: "TEXT", nullable: false),
                    auth = table.Column<string>(type: "TEXT", nullable: false),
                    vapid_key_id = table.Column<string>(type: "TEXT", nullable: false),
                    device_label = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "active"),
                    created_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    last_success_at_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    last_failure_at_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    last_status = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.id);
                    table.CheckConstraint("ck_push_subscriptions_state", "state IN ('active', 'stale_key', 'rejected')");
                    table.ForeignKey(
                        name: "FK_push_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "reminder_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    channel = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    ttl_s = table.Column<int>(type: "INTEGER", nullable: true),
                    expires_at_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    skip_available = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_log", x => x.id);
                    table.CheckConstraint("ck_reminder_log_channel", "channel IN ('webpush')");
                    table.CheckConstraint("ck_reminder_log_kind", "kind IN ('daily')");
                    table.CheckConstraint("ck_reminder_log_result", "result IN ('sending', 'sent', 'failed', 'gone', 'window_missed', 'no_target')");
                    table.ForeignKey(
                        name: "FK_reminder_log_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "reminder_settings",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    reminder_time = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "22:00"),
                    muted_until = table.Column<string>(type: "TEXT", nullable: true),
                    send_window_min = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 90),
                    updated_at_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_settings", x => x.user_id);
                    table.CheckConstraint("ck_reminder_settings_time", "length(reminder_time) = 5");
                    table.CheckConstraint("ck_reminder_settings_window", "send_window_min BETWEEN 15 AND 240");
                    table.ForeignKey(
                        name: "FK_reminder_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "notification_actions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    reminder_log_id = table.Column<int>(type: "INTEGER", nullable: true),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    mute_choice = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_actions", x => x.id);
                    table.CheckConstraint("ck_notification_actions_action", "action IN ('open', 'skip', 'mute')");
                    table.CheckConstraint("ck_notification_actions_mute_choice", "mute_choice IN ('tomorrow', '3d', '7d', 'disable', 'time_changed', 'cancel')");
                    table.CheckConstraint("ck_notification_actions_result", "result IN ('ok', 'skip_unavailable', 'already_done', 'unauthenticated', 'error')");
                    table.ForeignKey(
                        name: "FK_notification_actions_reminder_log_reminder_log_id",
                        column: x => x.reminder_log_id,
                        principalTable: "reminder_log",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_notification_actions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "push_deliveries",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    reminder_log_id = table.Column<int>(type: "INTEGER", nullable: false),
                    subscription_id = table.Column<int>(type: "INTEGER", nullable: true),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    last_status = table.Column<int>(type: "INTEGER", nullable: true),
                    updated_at_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_deliveries", x => x.id);
                    table.CheckConstraint("ck_push_deliveries_result", "result IN ('pending', 'sent', 'gone', 'rejected', 'transient')");
                    table.ForeignKey(
                        name: "FK_push_deliveries_push_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "push_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_push_deliveries_reminder_log_reminder_log_id",
                        column: x => x.reminder_log_id,
                        principalTable: "reminder_log",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_actions_reminder_log_id",
                table: "notification_actions",
                column: "reminder_log_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_actions_user_id",
                table: "notification_actions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_push_deliveries_reminder_log_id_subscription_id",
                table: "push_deliveries",
                columns: new[] { "reminder_log_id", "subscription_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_push_deliveries_subscription_id",
                table: "push_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_endpoint",
                table: "push_subscriptions",
                column: "endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_user",
                table: "push_subscriptions",
                columns: new[] { "user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_log_user_id_local_date_kind_channel",
                table: "reminder_log",
                columns: new[] { "user_id", "local_date", "kind", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_actions");

            migrationBuilder.DropTable(
                name: "push_deliveries");

            migrationBuilder.DropTable(
                name: "reminder_settings");

            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropTable(
                name: "reminder_log");
        }
    }
}
