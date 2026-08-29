using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class CreateDepositTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 31, 10, 26, 21, 353, DateTimeKind.Local).AddTicks(1407),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 30, 11, 2, 41, 781, DateTimeKind.Local).AddTicks(788));

            migrationBuilder.CreateTable(
                name: "DepositRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReceiptImagePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    WorkerNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedByWorkerId = table.Column<int>(type: "int", nullable: true),
                    WorkerReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BankNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedByBankId = table.Column<int>(type: "int", nullable: true),
                    BankReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepositRequests_Users_ReviewedByBankId",
                        column: x => x.ReviewedByBankId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositRequests_Users_ReviewedByWorkerId",
                        column: x => x.ReviewedByWorkerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepositRequests_ReviewedByBankId",
                table: "DepositRequests",
                column: "ReviewedByBankId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositRequests_ReviewedByWorkerId",
                table: "DepositRequests",
                column: "ReviewedByWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositRequests_UserId",
                table: "DepositRequests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepositRequests");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 30, 11, 2, 41, 781, DateTimeKind.Local).AddTicks(788),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 31, 10, 26, 21, 353, DateTimeKind.Local).AddTicks(1407));
        }
    }
}
