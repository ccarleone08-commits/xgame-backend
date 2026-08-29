using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class CreateWithdrawTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 31, 15, 54, 16, 438, DateTimeKind.Local).AddTicks(9429),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 31, 10, 26, 21, 353, DateTimeKind.Local).AddTicks(1407));

            migrationBuilder.CreateTable(
                name: "WithdrawRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WalletAddress = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    WorkerNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BankNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedByWorkerId = table.Column<int>(type: "int", nullable: true),
                    ReviewedByBankId = table.Column<int>(type: "int", nullable: true),
                    WorkerReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BankReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WithdrawRequests_Users_ReviewedByBankId",
                        column: x => x.ReviewedByBankId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WithdrawRequests_Users_ReviewedByWorkerId",
                        column: x => x.ReviewedByWorkerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WithdrawRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawRequests_ReviewedByBankId",
                table: "WithdrawRequests",
                column: "ReviewedByBankId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawRequests_ReviewedByWorkerId",
                table: "WithdrawRequests",
                column: "ReviewedByWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawRequests_UserId",
                table: "WithdrawRequests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WithdrawRequests");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 31, 10, 26, 21, 353, DateTimeKind.Local).AddTicks(1407),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 31, 15, 54, 16, 438, DateTimeKind.Local).AddTicks(9429));
        }
    }
}
