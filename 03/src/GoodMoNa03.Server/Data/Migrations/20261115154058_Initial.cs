using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa03.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    WordsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SttModel = table.Column<string>(type: "TEXT", nullable: true),
                    SttRuntime = table.Column<string>(type: "TEXT", nullable: true),
                    SttElapsedMs = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadedById = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadedByName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Titles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    VideoId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Views = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Titles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CutsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    HookCandidatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Hook = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    TitleCandidatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Hashtags = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EmphasisJson = table.Column<string>(type: "TEXT", nullable: false),
                    AiNote = table.Column<string>(type: "TEXT", nullable: true),
                    HasPatient = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsentConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutputDurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviewVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByName = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedById = table.Column<int>(type: "INTEGER", nullable: true),
                    ApprovedByName = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", nullable: true),
                    RejectedByName = table.Column<string>(type: "TEXT", nullable: true),
                    YouTubeUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RelatedVideoLinked = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PublishedByName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drafts_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DraftId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Where = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Excerpt = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Blocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ResolvedByName = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Findings_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_Code",
                table: "Drafts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_SourceId",
                table: "Drafts",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_Status",
                table: "Drafts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_DraftId",
                table: "Findings",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_VideoId",
                table: "Titles",
                column: "VideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "Titles");

            migrationBuilder.DropTable(
                name: "Drafts");

            migrationBuilder.DropTable(
                name: "Sources");
        }
    }
}
