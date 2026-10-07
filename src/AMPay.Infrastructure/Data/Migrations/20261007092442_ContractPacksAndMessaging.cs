using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContractPacksAndMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreditLifeAdministrator",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreditLifeUnderwriter",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalAddress",
                table: "Tenants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalAddress",
                table: "Tenants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatNumber",
                table: "Tenants",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContractTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Issue = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TemplatesApproved = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentChannels = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AccessTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccessExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstViewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OtpExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpFailedAttempts = table.Column<int>(type: "int", nullable: false),
                    SignatureMethod = table.Column<int>(type: "int", nullable: false),
                    SignedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SignedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignedIdNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SignedMobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SignedIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SignedUserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SignedRecordedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignedCopyDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanContracts_ClientDocuments_SignedCopyDocumentId",
                        column: x => x.SignedCopyDocumentId,
                        principalTable: "ClientDocuments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LoanContracts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanContracts_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanContracts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    To = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractTemplates_TenantId_Kind",
                table: "ContractTemplates",
                columns: new[] { "TenantId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_AccessTokenHash",
                table: "LoanContracts",
                column: "AccessTokenHash",
                unique: true,
                filter: "[AccessTokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_ClientId",
                table: "LoanContracts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_LoanId_Issue",
                table: "LoanContracts",
                columns: new[] { "LoanId", "Issue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_Reference",
                table: "LoanContracts",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_SignedCopyDocumentId",
                table: "LoanContracts",
                column: "SignedCopyDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanContracts_TenantId",
                table: "LoanContracts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundMessages_Context",
                table: "OutboundMessages",
                column: "Context");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundMessages_CreatedUtc",
                table: "OutboundMessages",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractTemplates");

            migrationBuilder.DropTable(
                name: "LoanContracts");

            migrationBuilder.DropTable(
                name: "OutboundMessages");

            migrationBuilder.DropColumn(
                name: "CreditLifeAdministrator",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CreditLifeUnderwriter",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PhysicalAddress",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PostalAddress",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "VatNumber",
                table: "Tenants");
        }
    }
}
