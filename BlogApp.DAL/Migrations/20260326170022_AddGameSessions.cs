using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddGameSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 26, 21, 0, 21, 686, DateTimeKind.Local).AddTicks(1519),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 2, 8, 23, 40, 52, 921, DateTimeKind.Local).AddTicks(6572));

            migrationBuilder.CreateTable(
                name: "GameSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    GameType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SessionEarnings = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SessionLossAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsWin = table.Column<bool>(type: "bit", nullable: false),
                    XpGained = table.Column<int>(type: "int", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameSessions_UserId_GameType",
                table: "GameSessions",
                columns: new[] { "UserId", "GameType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameSessions");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 2, 8, 23, 40, 52, 921, DateTimeKind.Local).AddTicks(6572),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 26, 21, 0, 21, 686, DateTimeKind.Local).AddTicks(1519));
        }
    }
}
