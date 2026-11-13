using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Photos_VisitId",
                table: "Photos");

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "Photos",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_VisitId_ContentHash",
                table: "Photos",
                columns: new[] { "VisitId", "ContentHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Photos_VisitId_ContentHash",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "Photos");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_VisitId",
                table: "Photos",
                column: "VisitId");
        }
    }
}
