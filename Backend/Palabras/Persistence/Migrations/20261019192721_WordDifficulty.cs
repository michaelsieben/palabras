using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <summary>
    /// The 8th migration: the intra-band authoring rank as <c>words.difficulty</c>.
    ///
    /// It is deliberately additive-only. A nullable <c>int</c> column is the one shape the EF SQLite
    /// provider translates without a table rebuild, so this is exactly one
    /// <c>ALTER TABLE "words" ADD "difficulty" INTEGER NULL</c>. No CHECK, no FK, no index: any of
    /// them would rebuild <c>words</c> (or force a rebuild of an indexed sibling table) and widen the
    /// crash window documented for ReviewGate in docs/08-upgrade-rollback.md §G. The column stays
    /// NULL for rows the old image inserts, and the new-card ordering coalesces NULL to 999.
    ///
    /// Do not replace the <c>AddColumn</c> with a provider operation that needs a rebuild. The schema
    /// test (<c>M3WordDifficultySchemaTests</c>) fails if the generated script contains
    /// <c>ef_temp_</c>, <c>PRAGMA foreign_keys</c>, <c>DROP COLUMN</c> or <c>RENAME COLUMN</c>.
    /// </summary>
    public partial class WordDifficulty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "difficulty",
                table: "words",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "difficulty",
                table: "words");
        }
    }
}
