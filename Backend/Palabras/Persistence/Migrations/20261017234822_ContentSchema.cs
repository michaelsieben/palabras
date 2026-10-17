using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "words",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    headword = table.Column<string>(type: "TEXT", nullable: false),
                    article = table.Column<string>(type: "TEXT", nullable: true),
                    gender = table.Column<string>(type: "TEXT", nullable: true),
                    pos = table.Column<string>(type: "TEXT", nullable: false),
                    cefr = table.Column<string>(type: "TEXT", nullable: false),
                    region_scope = table.Column<string>(type: "TEXT", nullable: false),
                    register = table.Column<string>(type: "TEXT", nullable: false),
                    register_notes = table.Column<string>(type: "TEXT", nullable: true),
                    region_notes = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    plural = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    provenance_method = table.Column<string>(type: "TEXT", nullable: false),
                    provenance_run = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_words", x => x.id);
                    table.CheckConstraint("ck_words_article", "article IN ('el', 'la', 'los', 'las')");
                    table.CheckConstraint("ck_words_gender", "gender IN ('masculine', 'feminine', 'common', 'not_applicable')");
                    table.CheckConstraint("ck_words_status", "status IN ('draft', 'pending_review', 'approved', 'needs_change', 'rejected', 'retired')");
                });

            migrationBuilder.CreateTable(
                name: "senses",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    sense_order = table.Column<int>(type: "INTEGER", nullable: false),
                    en_gloss = table.Column<string>(type: "TEXT", nullable: false),
                    en_accepted = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    es_accepted = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    example_es = table.Column<string>(type: "TEXT", nullable: false),
                    example_en = table.Column<string>(type: "TEXT", nullable: false),
                    accents_are_the_point = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_senses", x => x.id);
                    table.ForeignKey(
                        name: "FK_senses_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_alternatives",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    gloss = table.Column<string>(type: "TEXT", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    target_key = table.Column<string>(type: "TEXT", nullable: true),
                    example = table.Column<string>(type: "TEXT", nullable: true),
                    rank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_alternatives", x => x.id);
                    table.ForeignKey(
                        name: "FK_word_alternatives_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_revisions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_revisions", x => x.id);
                    table.CheckConstraint("ck_word_revisions_action", "action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire')");
                    table.ForeignKey(
                        name: "FK_word_revisions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_word_revisions_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_topics",
                columns: table => new
                {
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    topic = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_topics", x => new { x.word_id, x.topic });
                    table.ForeignKey(
                        name: "FK_word_topics_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_senses_word_id_sense_order",
                table: "senses",
                columns: new[] { "word_id", "sense_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_word_alternatives_word_id",
                table: "word_alternatives",
                column: "word_id");

            migrationBuilder.CreateIndex(
                name: "IX_word_revisions_actor_user_id",
                table: "word_revisions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_word_revisions_word_id",
                table: "word_revisions",
                column: "word_id");

            migrationBuilder.CreateIndex(
                name: "IX_words_key",
                table: "words",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_words_status",
                table: "words",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "senses");

            migrationBuilder.DropTable(
                name: "word_alternatives");

            migrationBuilder.DropTable(
                name: "word_revisions");

            migrationBuilder.DropTable(
                name: "word_topics");

            migrationBuilder.DropTable(
                name: "words");
        }
    }
}
