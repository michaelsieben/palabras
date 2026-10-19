using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <summary>
    /// The 7th migration (M3b council import, design-amendment.md ΔF:556-583). It is deliberately
    /// additive-only: every change to an existing table is a raw <c>ALTER TABLE … ADD COLUMN</c> with
    /// its CHECK (and, for <c>words.council_run_id</c>, its REFERENCES) inline.
    ///
    /// The EF SQLite provider would otherwise translate <c>AddCheckConstraint</c>/<c>AddForeignKey</c>
    /// and <c>DropColumn</c> into a full table rebuild with <c>PRAGMA foreign_keys = 0</c> outside the
    /// migration transaction — the exact crash window documented for ReviewGate in
    /// docs/08-upgrade-rollback.md §G. There is no reason to pay it twice in one release, and the
    /// additive shape keeps the old image able to INSERT against the migrated database.
    ///
    /// Do not replace these <c>Sql</c> calls with provider operations. The schema test
    /// (<c>M3CouncilSchemaTests.UpgradeFromReviewGateIsAdditiveAndRerunSafeAsync</c>) fails if the
    /// generated script contains <c>ef_temp_</c>, <c>PRAGMA foreign_keys</c>, <c>DROP COLUMN</c> or
    /// <c>RENAME COLUMN</c>.
    /// </summary>
    public partial class CouncilImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new tables come first: the words.council_run_id REFERENCES below needs the principal
            // table to exist, or SQLite rejects the ALTER.
            migrationBuilder.CreateTable(
                name: "council_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    run_id = table.Column<string>(type: "TEXT", nullable: false),
                    mode = table.Column<string>(type: "TEXT", nullable: false),
                    prompt_versions = table.Column<string>(type: "TEXT", nullable: false),
                    style_guide_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    topic = table.Column<string>(type: "TEXT", nullable: true),
                    cefr = table.Column<string>(type: "TEXT", nullable: true),
                    requested_count = table.Column<int>(type: "INTEGER", nullable: true),
                    models_requested = table.Column<int>(type: "INTEGER", nullable: false),
                    models_succeeded_propose = table.Column<int>(type: "INTEGER", nullable: false),
                    models_succeeded_critique = table.Column<int>(type: "INTEGER", nullable: false),
                    reduced_confidence = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    candidates_count = table.Column<int>(type: "INTEGER", nullable: false),
                    dropped_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    candidates_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    imported_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_council_runs", x => x.id);
                    table.CheckConstraint("ck_council_runs_mode", "mode IN ('propose', 'rework')");
                });

            migrationBuilder.CreateTable(
                name: "council_model_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    council_run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    model_id = table.Column<string>(type: "TEXT", nullable: false),
                    phase = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    input_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    output_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    cost_usd_est = table.Column<double>(type: "REAL", nullable: true),
                    invalid_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_council_model_results", x => x.id);
                    table.CheckConstraint("ck_council_model_results_phase", "phase IN ('propose', 'critique', 'topup')");
                    table.CheckConstraint("ck_council_model_results_status", "status IN ('ok', 'failed', 'timeout', 'invalid')");
                    table.ForeignKey(
                        name: "FK_council_model_results_council_runs_council_run_id",
                        column: x => x.council_run_id,
                        principalTable: "council_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_provenance",
                columns: table => new
                {
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    word_version = table.Column<int>(type: "INTEGER", nullable: false),
                    council_run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    model_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    issues = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_provenance", x => new { x.word_id, x.word_version, x.council_run_id, x.model_id, x.role });
                    table.CheckConstraint("ck_word_provenance_role", "role IN ('proposed', 'critique_ok', 'critique_issue')");
                    table.ForeignKey(
                        name: "FK_word_provenance_council_runs_council_run_id",
                        column: x => x.council_run_id,
                        principalTable: "council_runs",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_word_provenance_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // words: source and council_run_id. Both are NOT NULL-with-default or NULL, so the old
            // image can keep inserting its 26-column row after a rollback.
            migrationBuilder.Sql(
                "ALTER TABLE \"words\" ADD COLUMN \"source\" TEXT NOT NULL DEFAULT 'manual' " +
                "CHECK (source IN ('council', 'manual', 'reviewer'));");
            migrationBuilder.Sql(
                "ALTER TABLE \"words\" ADD COLUMN \"council_run_id\" INTEGER NULL " +
                "REFERENCES \"council_runs\" (\"id\");");

            // word_alternatives: alternatives v2 (ΔE.3). No backfill: legacy rows keep field=NULL,
            // word_version=0, support=0, source='manual' and resolve through the kind fallback.
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"field\" TEXT NULL " +
                "CHECK (field IN ('lemma', 'gloss', 'example', 'regional_note', 'regional_variant', 'register_variant'));");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"support\" INTEGER NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"models\" TEXT NULL;");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"region\" TEXT NULL CHECK (region IN ('ES', 'ES-NA'));");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"register\" TEXT NULL CHECK (register IN ('neutral', 'colloquial', 'formal'));");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"word_version\" INTEGER NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(
                "ALTER TABLE \"word_alternatives\" ADD COLUMN \"source\" TEXT NOT NULL DEFAULT 'manual' " +
                "CHECK (source IN ('council', 'reviewer', 'manual', 'previous'));");

            migrationBuilder.CreateIndex(
                name: "ix_words_council_run_id",
                table: "words",
                column: "council_run_id");

            // Index only, no FK: adding the constraint would rebuild word_reviews (Q-M3-3).
            migrationBuilder.CreateIndex(
                name: "ix_word_reviews_council_run_id",
                table: "word_reviews",
                column: "council_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_council_model_results_council_run_id_model_id_phase",
                table: "council_model_results",
                columns: new[] { "council_run_id", "model_id", "phase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_council_runs_created_at",
                table: "council_runs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_council_runs_run_id",
                table: "council_runs",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_word_provenance_run",
                table: "word_provenance",
                column: "council_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The exact inverse, still without a rebuild: dropping a column takes its inline CHECK and
            // REFERENCES with it, so the child column goes before the new parent table.
            migrationBuilder.DropIndex(
                name: "ix_words_council_run_id",
                table: "words");

            migrationBuilder.DropIndex(
                name: "ix_word_reviews_council_run_id",
                table: "word_reviews");

            migrationBuilder.Sql("ALTER TABLE \"words\" DROP COLUMN \"council_run_id\";");
            migrationBuilder.Sql("ALTER TABLE \"words\" DROP COLUMN \"source\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"field\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"support\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"models\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"region\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"register\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"word_version\";");
            migrationBuilder.Sql("ALTER TABLE \"word_alternatives\" DROP COLUMN \"source\";");

            migrationBuilder.DropTable(
                name: "word_provenance");

            migrationBuilder.DropTable(
                name: "council_model_results");

            migrationBuilder.DropTable(
                name: "council_runs");
        }
    }
}
