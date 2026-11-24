using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientPortalAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PortalAccessUtc",
                table: "Clients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PortalPublishedUtc",
                table: "Clients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortalStatementHash",
                table: "Clients",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PortalAccessUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PortalPublishedUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PortalStatementHash",
                table: "Clients");
        }
    }
}
