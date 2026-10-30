using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditQuotes",
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
                    TermsHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentChannels = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AccessTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FirstViewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OtpExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpFailedAttempts = table.Column<int>(type: "int", nullable: false),
                    OtpWindowStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpSendsInWindow = table.Column<int>(type: "int", nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptanceMethod = table.Column<int>(type: "int", nullable: false),
                    ConsentsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AcceptedMobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AcceptedIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AcceptedUserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeclinedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VoidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditQuotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditQuotes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditQuotes_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditQuotes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditQuotes_AccessTokenHash",
                table: "CreditQuotes",
                column: "AccessTokenHash",
                unique: true,
                filter: "[AccessTokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditQuotes_ClientId",
                table: "CreditQuotes",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditQuotes_LoanId_Issue",
                table: "CreditQuotes",
                columns: new[] { "LoanId", "Issue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditQuotes_Reference",
                table: "CreditQuotes",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditQuotes_TenantId_Status",
                table: "CreditQuotes",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditQuotes");
        }
    }
}
