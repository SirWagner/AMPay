using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GoodToGoScreening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScreeningRecheckDays",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FollowUpDoneBy",
                table: "ClientNotes",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpDoneUtc",
                table: "ClientNotes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpOn",
                table: "ClientNotes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContractExpiryDate",
                table: "ClientEmployments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmployerId",
                table: "ClientEmployments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtherEmployerReference",
                table: "ClientEmployments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPoint",
                table: "ClientEmployments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPointNumber",
                table: "ClientEmployments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorkTelephoneNotAvailable",
                table: "ClientEmployments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "YearsAtPreviousEmployer",
                table: "ClientEmployments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankStatementAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Flag = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Observations = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AverageMonthlyIncome = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AverageMonthlySpend = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    ConsentRecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankStatementAssessments_ClientDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ClientDocuments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BankStatementAssessments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientScreenings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    IsFirstTimeApplicant = table.Column<bool>(type: "bit", nullable: false),
                    Checks = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CheckedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OverrideReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OverriddenBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OverriddenUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientScreenings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientScreenings_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CheckedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientVerifications_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Employers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PayPoint = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Telephone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ContactPerson = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EmailAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PayFrequency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SalaryDay = table.Column<int>(type: "int", nullable: true),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientNotes_FollowUpOn_FollowUpDoneUtc",
                table: "ClientNotes",
                columns: new[] { "FollowUpOn", "FollowUpDoneUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientEmployments_EmployerId",
                table: "ClientEmployments",
                column: "EmployerId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementAssessments_ClientId",
                table: "BankStatementAssessments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementAssessments_DocumentId",
                table: "BankStatementAssessments",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientScreenings_ClientId_CheckedUtc",
                table: "ClientScreenings",
                columns: new[] { "ClientId", "CheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientVerifications_ClientId_Kind_CheckedUtc",
                table: "ClientVerifications",
                columns: new[] { "ClientId", "Kind", "CheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Employers_TenantId_Name",
                table: "Employers",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_ClientEmployments_Employers_EmployerId",
                table: "ClientEmployments",
                column: "EmployerId",
                principalTable: "Employers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientEmployments_Employers_EmployerId",
                table: "ClientEmployments");

            migrationBuilder.DropTable(
                name: "BankStatementAssessments");

            migrationBuilder.DropTable(
                name: "ClientScreenings");

            migrationBuilder.DropTable(
                name: "ClientVerifications");

            migrationBuilder.DropTable(
                name: "Employers");

            migrationBuilder.DropIndex(
                name: "IX_ClientNotes_FollowUpOn_FollowUpDoneUtc",
                table: "ClientNotes");

            migrationBuilder.DropIndex(
                name: "IX_ClientEmployments_EmployerId",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "ScreeningRecheckDays",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "FollowUpDoneBy",
                table: "ClientNotes");

            migrationBuilder.DropColumn(
                name: "FollowUpDoneUtc",
                table: "ClientNotes");

            migrationBuilder.DropColumn(
                name: "FollowUpOn",
                table: "ClientNotes");

            migrationBuilder.DropColumn(
                name: "ContractExpiryDate",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "EmployerId",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "OtherEmployerReference",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "PayPoint",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "PayPointNumber",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "WorkTelephoneNotAvailable",
                table: "ClientEmployments");

            migrationBuilder.DropColumn(
                name: "YearsAtPreviousEmployer",
                table: "ClientEmployments");
        }
    }
}
