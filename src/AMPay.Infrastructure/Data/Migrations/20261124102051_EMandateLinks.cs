using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EMandateLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignedPdfUrl",
                table: "Mandates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SigningUrl",
                table: "Mandates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignedPdfUrl",
                table: "Mandates");

            migrationBuilder.DropColumn(
                name: "SigningUrl",
                table: "Mandates");
        }
    }
}
