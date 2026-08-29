using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class RankDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2025, 11, 22, 21, 56, 18, 848, DateTimeKind.Local).AddTicks(7355),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2025, 11, 7, 3, 44, 40, 280, DateTimeKind.Local).AddTicks(7907));

            migrationBuilder.CreateTable(
                name: "GameStatistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    TotalWinnings = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalLosses = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalGamesPlayed = table.Column<int>(type: "int", nullable: false),
                    TotalGamesWon = table.Column<int>(type: "int", nullable: false),
                    WeeklyWinnings = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WeeklyGamesPlayed = table.Column<int>(type: "int", nullable: false),
                    WeeklyGamesWon = table.Column<int>(type: "int", nullable: false),
                    WeekStart = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    GameBreakdown = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "{}"),
                    LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameStatistics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameStatistics_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerRanks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    GameType = table.Column<int>(type: "int", nullable: false),
                    CurrentRank = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Beginner"),
                    RankLevel = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    ExperiencePoints = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    RequiredXPForNextRank = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    TotalGamesPlayed = table.Column<int>(type: "int", nullable: false),
                    TotalWins = table.Column<int>(type: "int", nullable: false),
                    TotalLosses = table.Column<int>(type: "int", nullable: false),
                    TotalEarnings = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WinRate = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    CurrentWinStreak = table.Column<int>(type: "int", nullable: false),
                    BestWinStreak = table.Column<int>(type: "int", nullable: false),
                    Top3Finishes = table.Column<int>(type: "int", nullable: false),
                    FirstPlaceFinishes = table.Column<int>(type: "int", nullable: false),
                    UnlockedAchievements = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    LastGamePlayed = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RankLastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRanks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerRanks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameStatistics_UserId",
                table: "GameStatistics",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameStatistics_WeekStart",
                table: "GameStatistics",
                column: "WeekStart");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRanks_CurrentRank",
                table: "PlayerRanks",
                column: "CurrentRank");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRanks_ExperiencePoints",
                table: "PlayerRanks",
                column: "ExperiencePoints");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRanks_GameType",
                table: "PlayerRanks",
                column: "GameType");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRanks_UserId_GameType",
                table: "PlayerRanks",
                columns: new[] { "UserId", "GameType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameStatistics");

            migrationBuilder.DropTable(
                name: "PlayerRanks");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2025, 11, 7, 3, 44, 40, 280, DateTimeKind.Local).AddTicks(7907),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2025, 11, 22, 21, 56, 18, 848, DateTimeKind.Local).AddTicks(7355));
        }
    }
}
