using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantSelfServiceLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdvisorWhatsApp",
                table: "Tenants",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PortalSyncedUtc",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelfServiceCode",
                table: "Tenants",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_SelfServiceCode",
                table: "Tenants",
                column: "SelfServiceCode",
                unique: true,
                filter: "[SelfServiceCode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_SelfServiceCode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AdvisorWhatsApp",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PortalSyncedUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SelfServiceCode",
                table: "Tenants");
        }
    }
}
