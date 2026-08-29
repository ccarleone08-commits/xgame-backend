using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogApp.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddManualCoinPaymentAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CoinPackageId",
                table: "PaymentTransactions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<decimal>(
                name: "CoinAmount",
                table: "PaymentTransactions",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("""
                UPDATE pt
                SET pt.CoinAmount = cp.CoinAmount
                FROM PaymentTransactions pt
                INNER JOIN CoinPackages cp ON cp.Id = pt.CoinPackageId
                WHERE pt.CoinAmount = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE PaymentTransactions
                SET CoinPackageId = 1
                WHERE CoinPackageId IS NULL
                """);

            migrationBuilder.DropColumn(
                name: "CoinAmount",
                table: "PaymentTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "CoinPackageId",
                table: "PaymentTransactions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
