using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoanCollectionsAndPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoanContracts_TenantId",
                table: "LoanContracts");

            migrationBuilder.AddColumn<int>(
                name: "CoolingOffBusinessDays",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<decimal>(
                name: "LendingCapital",
                table: "Tenants",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmount",
                table: "LoanScheduleEntries",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidUtc",
                table: "LoanScheduleEntries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WrittenOffAmount",
                table: "Loans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WrittenOffUtc",
                table: "Loans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoanCollections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ActionDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    FailureGroup = table.Column<int>(type: "int", nullable: true),
                    RecoveredByCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NetcashBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayNowTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanCollections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanCollections_LoanScheduleEntries_ScheduleEntryId",
                        column: x => x.ScheduleEntryId,
                        principalTable: "LoanScheduleEntries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LoanCollections_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanCollections_NetcashBatches_NetcashBatchId",
                        column: x => x.NetcashBatchId,
                        principalTable: "NetcashBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LoanCollections_PayNowTransactions_PayNowTransactionId",
                        column: x => x.PayNowTransactionId,
                        principalTable: "PayNowTransactions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LoanCollections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActiveLoans = table.Column<int>(type: "int", nullable: false),
                    BookOutstanding = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Arrears1To30Count = table.Column<int>(type: "int", nullable: false),
                    Arrears1To30 = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Arrears31To60Count = table.Column<int>(type: "int", nullable: false),
                    Arrears31To60 = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Arrears61To90Count = table.Column<int>(type: "int", nullable: false),
                    Arrears61To90 = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Arrears90PlusCount = table.Column<int>(type: "int", nullable: false),
                    Arrears90Plus = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioSnapshots_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayNowTransactions_TenantId_Status_CompletedUtc",
                table: "PayNowTransactions",
                columns: new[] { "TenantId", "Status", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanScheduleEntries_DueDate",
                table: "LoanScheduleEntries",
                column: "DueDate")
                .Annotation("SqlServer:Include", new[] { "LoanId", "InstalmentTotal", "PaidAmount" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_TenantId_DecidedUtc",
                table: "Loans",
                columns: new[] { "TenantId", "DecidedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_TenantId_DisbursedUtc",
                table: "Loans",
                columns: new[] { "TenantId", "DisbursedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_TenantId_Status",
                table: "LoanContracts",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_LoanId_ActionDate",
                table: "LoanCollections",
                columns: new[] { "LoanId", "ActionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_NetcashBatchId",
                table: "LoanCollections",
                column: "NetcashBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_PayNowTransactionId",
                table: "LoanCollections",
                column: "PayNowTransactionId",
                unique: true,
                filter: "[PayNowTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_ScheduleEntryId",
                table: "LoanCollections",
                column: "ScheduleEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_TenantId_ActionDate",
                table: "LoanCollections",
                columns: new[] { "TenantId", "ActionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanCollections_TenantId_Status_ActionDate",
                table: "LoanCollections",
                columns: new[] { "TenantId", "Status", "ActionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSnapshots_TenantId_SnapshotDate",
                table: "PortfolioSnapshots",
                columns: new[] { "TenantId", "SnapshotDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanCollections");

            migrationBuilder.DropTable(
                name: "PortfolioSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PayNowTransactions_TenantId_Status_CompletedUtc",
                table: "PayNowTransactions");

            migrationBuilder.DropIndex(
                name: "IX_LoanScheduleEntries_DueDate",
                table: "LoanScheduleEntries");

            migrationBuilder.DropIndex(
                name: "IX_Loans_TenantId_DecidedUtc",
                table: "Loans");

            migrationBuilder.DropIndex(
                name: "IX_Loans_TenantId_DisbursedUtc",
                table: "Loans");

            migrationBuilder.DropIndex(
                name: "IX_LoanContracts_TenantId_Status",
                table: "LoanContracts");

            migrationBuilder.DropColumn(
                name: "CoolingOffBusinessDays",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LendingCapital",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PaidAmount",
                table: "LoanScheduleEntries");

            migrationBuilder.DropColumn(
                name: "PaidUtc",
                table: "LoanScheduleEntries");

            migrationBuilder.DropColumn(
                name: "WrittenOffAmount",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "WrittenOffUtc",
                table: "Loans");

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_TenantId",
                table: "LoanContracts",
                column: "TenantId");
        }
    }
}
