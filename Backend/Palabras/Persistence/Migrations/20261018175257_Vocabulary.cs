using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Vocabulary : Migration
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
                    external_id = table.Column<string>(type: "TEXT", nullable: false),
                    headword = table.Column<string>(type: "TEXT", nullable: false),
                    article = table.Column<string>(type: "TEXT", nullable: true),
                    gender = table.Column<string>(type: "TEXT", nullable: true),
                    part_of_speech = table.Column<string>(type: "TEXT", nullable: false),
                    cefr = table.Column<string>(type: "TEXT", nullable: false),
                    region_variant = table.Column<string>(type: "TEXT", nullable: false),
                    region_scope = table.Column<string>(type: "TEXT", nullable: false),
                    region_notes = table.Column<string>(type: "TEXT", nullable: true),
                    register = table.Column<string>(type: "TEXT", nullable: false),
                    register_notes = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    content_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_words", x => x.id);
                    table.CheckConstraint("ck_words_status", "status IN ('draft','pending_review','approved','rejected','needs_change')");
                });

            migrationBuilder.CreateTable(
                name: "alternatives",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    gloss = table.Column<string>(type: "TEXT", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: true),
                    example = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alternatives", x => x.id);
                    table.CheckConstraint("ck_alternatives_kind", "kind IN ('synonym','near-synonym','different-sense','regional','register')");
                    table.ForeignKey(
                        name: "FK_alternatives_words_word_id",
                        column: x => x.word_id,
                        principalTable: "words",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "senses",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    word_id = table.Column<int>(type: "INTEGER", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    gloss = table.Column<string>(type: "TEXT", nullable: false),
                    example = table.Column<string>(type: "TEXT", nullable: false),
                    example_gloss = table.Column<string>(type: "TEXT", nullable: false),
                    register = table.Column<string>(type: "TEXT", nullable: true),
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
                name: "IX_alternatives_word_id_position",
                table: "alternatives",
                columns: new[] { "word_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_senses_word_id_position",
                table: "senses",
                columns: new[] { "word_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_word_topics_topic_word_id",
                table: "word_topics",
                columns: new[] { "topic", "word_id" });

            migrationBuilder.CreateIndex(
                name: "IX_words_cefr",
                table: "words",
                column: "cefr");

            migrationBuilder.CreateIndex(
                name: "IX_words_external_id",
                table: "words",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_words_part_of_speech",
                table: "words",
                column: "part_of_speech");

            migrationBuilder.CreateIndex(
                name: "IX_words_status",
                table: "words",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alternatives");

            migrationBuilder.DropTable(
                name: "senses");

            migrationBuilder.DropTable(
                name: "word_topics");

            migrationBuilder.DropTable(
                name: "words");
        }
    }
}
