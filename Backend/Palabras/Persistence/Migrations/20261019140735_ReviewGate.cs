using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_word_revisions_action",
                table: "word_revisions");

            migrationBuilder.AddColumn<int>(
                name: "approved_version",
                table: "words",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "council_agreement",
                table: "words",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "council_band",
                table: "words",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lint_warnings",
                table: "words",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reduced_confidence",
                table: "words",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "status_changed_at",
                table: "words",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "word_reviews",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    word_version = table.Column<int>(type: "INTEGER", nullable: false),
                    sense_order = table.Column<int>(type: "INTEGER", nullable: true),
                    reviewer_id = table.Column<int>(type: "INTEGER", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    field = table.Column<string>(type: "TEXT", nullable: true),
                    chosen_alternative_id = table.Column<int>(type: "INTEGER", nullable: true),
                    edit_diff = table.Column<string>(type: "TEXT", nullable: true),
                    note = table.Column<string>(type: "TEXT", nullable: true),
                    client_review_id = table.Column<string>(type: "TEXT", nullable: false),
                    via_batch = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    council_run_id = table.Column<int>(type: "INTEGER", nullable: true),
                    agreement_at_review = table.Column<double>(type: "REAL", nullable: true),
                    decision_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_reviews", x => x.id);
                    table.CheckConstraint("ck_word_reviews_action", "action IN ('approve', 'approve_alt', 'approve_edit', 'reject', 'needs_change', 'demote')");
                    table.CheckConstraint("ck_word_reviews_field", "field IN ('lemma', 'gloss', 'example', 'regional_note', 'regional_variant', 'register_variant', 'article', 'gender', 'accepted', 'whole')");
                    table.ForeignKey(
                        name: "FK_word_reviews_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_word_reviews_word_alternatives_chosen_alternative_id",
                        column: x => x.chosen_alternative_id,
                        principalTable: "word_alternatives",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_word_reviews_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_words_council_band",
                table: "words",
                sql: "council_band IN ('alta', 'media')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_word_revisions_action",
                table: "word_revisions",
                sql: "action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire', 'demote')");

            migrationBuilder.CreateIndex(
                name: "IX_word_reviews_chosen_alternative_id",
                table: "word_reviews",
                column: "chosen_alternative_id");

            migrationBuilder.CreateIndex(
                name: "IX_word_reviews_client_review_id",
                table: "word_reviews",
                column: "client_review_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_word_reviews_reviewer_id",
                table: "word_reviews",
                column: "reviewer_id");

            migrationBuilder.CreateIndex(
                name: "IX_word_reviews_word_id_word_version",
                table: "word_reviews",
                columns: new[] { "word_id", "word_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "word_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_words_council_band",
                table: "words");

            migrationBuilder.DropCheckConstraint(
                name: "ck_word_revisions_action",
                table: "word_revisions");

            migrationBuilder.DropColumn(
                name: "approved_version",
                table: "words");

            migrationBuilder.DropColumn(
                name: "council_agreement",
                table: "words");

            migrationBuilder.DropColumn(
                name: "council_band",
                table: "words");

            migrationBuilder.DropColumn(
                name: "lint_warnings",
                table: "words");

            migrationBuilder.DropColumn(
                name: "reduced_confidence",
                table: "words");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "words");

            migrationBuilder.AddCheckConstraint(
                name: "ck_word_revisions_action",
                table: "word_revisions",
                sql: "action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire')");
        }
    }
}
