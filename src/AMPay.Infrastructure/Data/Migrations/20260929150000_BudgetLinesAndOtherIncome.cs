using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Turns ClientBudgets into the line-by-line budget captured on the Financial step, and
    /// records other income on each affordability assessment.
    /// <para>
    /// Adds columns only, so it is absent from the DatabaseInitializer schema markers and
    /// always runs normally.
    /// </para>
    /// </summary>
    public partial class BudgetLinesAndOtherIncome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ClientBudgets",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "ClientBudgets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ClientBudgets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Zero is not a BudgetLineKind. Nothing wrote budget rows before this - the
            // Budgets tab was never built - but any that exist are expenses, which is what
            // the tab described them as.
            migrationBuilder.Sql("UPDATE ClientBudgets SET Kind = 1 WHERE Kind = 0;");

            migrationBuilder.AddColumn<decimal>(
                name: "OtherMonthlyIncome",
                table: "AffordabilityAssessments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "ClientBudgets");

            migrationBuilder.DropColumn(
                name: "DisplayOrder",
                table: "ClientBudgets");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ClientBudgets");

            migrationBuilder.DropColumn(
                name: "OtherMonthlyIncome",
                table: "AffordabilityAssessments");
        }
    }
}
