using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class VisitReportedById : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReportedById",
                table: "Visits",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReportedById",
                table: "Visits");
        }
    }
}
