using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditEnquiryStatusAndConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentRecordedUtc",
                table: "ClientCreditEnquiries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReasonCode",
                table: "ClientCreditEnquiries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportReceivedUtc",
                table: "ClientCreditEnquiries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ClientCreditEnquiries",
                type: "int",
                nullable: false,
                defaultValue: 1); // enquiries recorded before this change are history, not pending
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsentRecordedUtc",
                table: "ClientCreditEnquiries");

            migrationBuilder.DropColumn(
                name: "ReasonCode",
                table: "ClientCreditEnquiries");

            migrationBuilder.DropColumn(
                name: "ReportReceivedUtc",
                table: "ClientCreditEnquiries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ClientCreditEnquiries");
        }
    }
}
