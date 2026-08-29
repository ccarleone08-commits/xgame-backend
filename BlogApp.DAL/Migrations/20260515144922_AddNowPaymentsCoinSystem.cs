using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddNowPaymentsCoinSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(2026, 3, 31, 15, 54, 16, 438, DateTimeKind.Local).AddTicks(9429));

            migrationBuilder.CreateTable(
                name: "CoinLedgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinLedgers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CoinPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CoinAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PriceAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PriceCurrency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "usd"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CoinPackageId = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NowPaymentsPaymentId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NowPaymentsInvoiceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PayCurrency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PriceAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PriceCurrency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ActuallyPaid = table.Column<decimal>(type: "decimal(18,8)", nullable: true),
                    PayAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CoinsGranted = table.Column<bool>(type: "bit", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RawIpnPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_CoinPackages_CoinPackageId",
                        column: x => x.CoinPackageId,
                        principalTable: "CoinPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CoinPackages",
                columns: new[] { "Id", "CoinAmount", "CreateDate", "IsActive", "IsDeleted", "Name", "PriceAmount", "PriceCurrency", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 100m, new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, false, "Starter", 5m, "usd", null },
                    { 2, 250m, new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, false, "Plus", 10m, "usd", null },
                    { 3, 600m, new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, false, "Pro", 20m, "usd", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoinLedgers_UserId_ReferenceId_Type",
                table: "CoinLedgers",
                columns: new[] { "UserId", "ReferenceId", "Type" },
                unique: true,
                filter: "[ReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_CoinPackageId",
                table: "PaymentTransactions",
                column: "CoinPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_NowPaymentsPaymentId",
                table: "PaymentTransactions",
                column: "NowPaymentsPaymentId",
                unique: true,
                filter: "[NowPaymentsPaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_OrderId",
                table: "PaymentTransactions",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_UserId",
                table: "PaymentTransactions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoinLedgers");

            migrationBuilder.DropTable(
                name: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "CoinPackages");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreateDate",
                table: "Categories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2026, 3, 31, 15, 54, 16, 438, DateTimeKind.Local).AddTicks(9429),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETDATE()");
        }
    }
}
