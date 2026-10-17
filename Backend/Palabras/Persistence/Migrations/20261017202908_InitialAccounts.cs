using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palabras.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    username = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('owner', 'reviewer')");
                    table.CheckConstraint("ck_users_username", "length(username) BETWEEN 3 AND 32 AND username = lower(trim(username)) COLLATE BINARY AND username NOT GLOB '*[^a-z0-9._-]*'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_role",
                table: "users",
                column: "role",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
