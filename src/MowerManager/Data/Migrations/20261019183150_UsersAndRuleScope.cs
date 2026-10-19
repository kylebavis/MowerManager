using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MowerManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsersAndRuleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "RuleSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.Sql("UPDATE RuleSettings SET Scope = CASE WHEN Enabled THEN 'AnyArea' ELSE 'Off' END;");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "RuleSettings");

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "RuleSettings");

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "RuleSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
