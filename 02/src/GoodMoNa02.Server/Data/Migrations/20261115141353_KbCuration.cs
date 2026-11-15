using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa02.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class KbCuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiDeclined",
                table: "AskLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Correction",
                table: "AskLogs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CuratedAt",
                table: "AskLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftItemId",
                table: "AskLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KbSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Phrases = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ProposedBody = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AskLogIds = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedByName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbSuggestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbWatchPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    LastText = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: true),
                    LastHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbWatchPages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KbSuggestions_Status",
                table: "KbSuggestions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_KbWatchPages_Url",
                table: "KbWatchPages",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KbSuggestions");

            migrationBuilder.DropTable(
                name: "KbWatchPages");

            migrationBuilder.DropColumn(
                name: "AiDeclined",
                table: "AskLogs");

            migrationBuilder.DropColumn(
                name: "Correction",
                table: "AskLogs");

            migrationBuilder.DropColumn(
                name: "CuratedAt",
                table: "AskLogs");

            migrationBuilder.DropColumn(
                name: "DraftItemId",
                table: "AskLogs");
        }
    }
}
