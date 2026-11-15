using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa02.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CsKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AskLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StaffId = table.Column<int>(type: "INTEGER", nullable: false),
                    StaffName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Found = table.Column<bool>(type: "INTEGER", nullable: false),
                    TopItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    TopCoverage = table.Column<double>(type: "REAL", nullable: false),
                    Answered = table.Column<bool>(type: "INTEGER", nullable: false),
                    Helpful = table.Column<bool>(type: "INTEGER", nullable: true),
                    FollowUp = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ResolvedItemId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AskLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Keywords = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ImportId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReviewBy = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedById = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedByName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbItems_KbImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "KbImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AskLogs_FollowUp",
                table: "AskLogs",
                column: "FollowUp");

            migrationBuilder.CreateIndex(
                name: "IX_AskLogs_StaffId_CreatedAt",
                table: "AskLogs",
                columns: new[] { "StaffId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KbItems_ImportId",
                table: "KbItems",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_KbItems_Status",
                table: "KbItems",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AskLogs");

            migrationBuilder.DropTable(
                name: "KbItems");

            migrationBuilder.DropTable(
                name: "KbImports");
        }
    }
}
