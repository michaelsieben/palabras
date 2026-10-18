using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DayStatusAndSkips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "day_skips",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    refunded_at_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_day_skips", x => x.id);
                    table.CheckConstraint("ck_day_skips_source", "source IN ('app', 'notification')");
                    table.ForeignKey(
                        name: "FK_day_skips_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "practice_sessions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    started_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_s = table.Column<int>(type: "INTEGER", nullable: true),
                    planned_s = table.Column<int>(type: "INTEGER", nullable: true),
                    new_count = table.Column<int>(type: "INTEGER", nullable: false),
                    review_count = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_sessions", x => x.id);
                    table.CheckConstraint("ck_practice_sessions_status", "status IN ('in_progress', 'completed', 'abandoned')");
                    table.ForeignKey(
                        name: "FK_practice_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "day_status",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<int>(type: "INTEGER", nullable: true),
                    decided_at_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_day_status", x => new { x.user_id, x.local_date });
                    table.CheckConstraint("ck_day_status_status", "status IN ('done', 'skipped', 'missed')");
                    table.ForeignKey(
                        name: "FK_day_status_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_day_status_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_day_skips_user_id_local_date",
                table: "day_skips",
                columns: new[] { "user_id", "local_date" },
                unique: true);

            // design-amendment.md ΔF:640 verbatim. Raw SQL because EF Core merges two indexes
            // declared over the same columns into one, and the rolling window must only see
            // consumed (non-refunded) skips. Dropped with the table in Down().
            migrationBuilder.Sql(
                "CREATE INDEX \"ix_day_skips_window\" ON \"day_skips\" (\"user_id\", \"local_date\") WHERE refunded_at_ms IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_day_status_session_id",
                table: "day_status",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_practice_sessions_user_id_local_date",
                table: "practice_sessions",
                columns: new[] { "user_id", "local_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "day_skips");

            migrationBuilder.DropTable(
                name: "day_status");

            migrationBuilder.DropTable(
                name: "practice_sessions");
        }
    }
}
