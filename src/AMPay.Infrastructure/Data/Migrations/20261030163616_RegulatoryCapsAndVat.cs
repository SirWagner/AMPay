using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RegulatoryCapsAndVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "VatPortion",
                table: "LoanScheduleEntries",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditCostMultiple",
                table: "Loans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "FeeOverrideByUserId",
                table: "Loans",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeeOverrideReason",
                table: "Loans",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FuneralCover",
                table: "Loans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ProductType",
                table: "Loans",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ThirdPartyPayment",
                table: "Loans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalVat",
                table: "Loans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRate",
                table: "Loans",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "RegulatoryCaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegulatoryCaps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryCaps_Type_EffectiveFrom",
                table: "RegulatoryCaps",
                columns: new[] { "Type", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegulatoryCaps");

            migrationBuilder.DropColumn(
                name: "VatPortion",
                table: "LoanScheduleEntries");

            migrationBuilder.DropColumn(
                name: "CreditCostMultiple",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "FeeOverrideByUserId",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "FeeOverrideReason",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "FuneralCover",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ProductType",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ThirdPartyPayment",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "TotalVat",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "Loans");
        }
    }
}
