using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodMoNa02.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Staff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Staff",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LoginId = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedRole = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FailedLogins = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedById = table.Column<int>(type: "INTEGER", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Staff", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StaffId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReplacedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffSessions_Staff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Staff_LoginId",
                table: "Staff",
                column: "LoginId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Staff_Status",
                table: "Staff",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSessions_StaffId",
                table: "StaffSessions",
                column: "StaffId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSessions_TokenHash",
                table: "StaffSessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffSessions");

            migrationBuilder.DropTable(
                name: "Staff");
        }
    }
}
