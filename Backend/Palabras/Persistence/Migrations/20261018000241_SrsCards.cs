using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SrsCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "timezone",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cards",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    sense_id = table.Column<int>(type: "INTEGER", nullable: false),
                    direction = table.Column<string>(type: "TEXT", nullable: false),
                    epoch = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    step = table.Column<int>(type: "INTEGER", nullable: true),
                    stability = table.Column<double>(type: "REAL", nullable: true),
                    difficulty = table.Column<double>(type: "REAL", nullable: true),
                    due_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    last_review_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    reps = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    lapses = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    suspended_reason = table.Column<string>(type: "TEXT", nullable: true),
                    buried_until_local_date = table.Column<string>(type: "TEXT", nullable: true),
                    is_leech = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    introduced_local_date = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cards", x => x.id);
                    table.CheckConstraint("ck_cards_direction", "direction IN ('es_en', 'en_es')");
                    table.CheckConstraint("ck_cards_state", "state IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_cards_suspended_reason", "suspended_reason IN ('word_not_approved', 'sense_removed', 'manual', 'leech')");
                    table.ForeignKey(
                        name: "FK_cards_senses_sense_id",
                        column: x => x.sense_id,
                        principalTable: "senses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cards_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    desired_retention = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.90000000000000002),
                    new_per_day = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 12),
                    max_reviews_per_day = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 200),
                    backlog_threshold = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 150),
                    session_target_s = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 600),
                    session_max_s = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 900),
                    max_interval_days = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 730),
                    enable_fuzz = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    learning_steps = table.Column<string>(type: "TEXT", nullable: true, defaultValue: "[\"00:01:00\",\"00:10:00\"]"),
                    relearning_steps = table.Column<string>(type: "TEXT", nullable: true, defaultValue: "[\"00:10:00\"]"),
                    leech_lapses = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 8),
                    weights_json = table.Column<string>(type: "TEXT", nullable: true),
                    weights_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_settings", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_user_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "review_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    client_attempt_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    card_id = table.Column<int>(type: "INTEGER", nullable: false),
                    card_epoch = table.Column<int>(type: "INTEGER", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: true),
                    rating = table.Column<int>(type: "INTEGER", nullable: false),
                    reviewed_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    received_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    elapsed_days = table.Column<double>(type: "REAL", nullable: false),
                    interval_before_days = table.Column<double>(type: "REAL", nullable: true),
                    interval_after_days = table.Column<double>(type: "REAL", nullable: false),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    mode = table.Column<string>(type: "TEXT", nullable: false),
                    is_correct_meaning = table.Column<bool>(type: "INTEGER", nullable: true),
                    is_correct_form = table.Column<bool>(type: "INTEGER", nullable: true),
                    feedback_kind = table.Column<string>(type: "TEXT", nullable: true),
                    overridden = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    answer_text = table.Column<string>(type: "TEXT", nullable: true),
                    state_before = table.Column<int>(type: "INTEGER", nullable: false),
                    stability_before = table.Column<double>(type: "REAL", nullable: true),
                    difficulty_before = table.Column<double>(type: "REAL", nullable: true),
                    state_after = table.Column<int>(type: "INTEGER", nullable: false),
                    stability_after = table.Column<double>(type: "REAL", nullable: true),
                    difficulty_after = table.Column<double>(type: "REAL", nullable: true),
                    due_after_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    weights_version = table.Column<int>(type: "INTEGER", nullable: false),
                    counts_for_srs = table.Column<bool>(type: "INTEGER", nullable: true, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_log", x => x.id);
                    table.CheckConstraint("ck_review_log_mode", "mode IN ('recognition', 'typed', 'cloze')");
                    table.CheckConstraint("ck_review_log_rating", "rating BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_review_log_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_review_log_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_review_log_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_cards_due",
                table: "cards",
                columns: new[] { "user_id", "due_ms" },
                filter: "suspended_reason IS NULL AND state <> 0");

            migrationBuilder.CreateIndex(
                name: "ix_cards_new",
                table: "cards",
                columns: new[] { "user_id", "created_at" },
                filter: "suspended_reason IS NULL AND state = 0");

            migrationBuilder.CreateIndex(
                name: "IX_cards_sense_id_user_id_direction",
                table: "cards",
                columns: new[] { "sense_id", "user_id", "direction" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_log_card_id_reviewed_at_ms",
                table: "review_log",
                columns: new[] { "card_id", "reviewed_at_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_review_log_client_attempt_id",
                table: "review_log",
                column: "client_attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_log_user_id_reviewed_at_ms",
                table: "review_log",
                columns: new[] { "user_id", "reviewed_at_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_review_log_word_id",
                table: "review_log",
                column: "word_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_log");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "cards");

            migrationBuilder.DropColumn(
                name: "timezone",
                table: "users");
        }
    }
}
