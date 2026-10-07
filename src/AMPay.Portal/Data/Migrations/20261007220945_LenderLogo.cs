using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Portal.Data.Migrations
{
    /// <inheritdoc />
    public partial class LenderLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LogoUpdatedUtc",
                table: "Lenders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LenderLogos",
                columns: table => new
                {
                    LenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LenderLogos", x => x.LenderId);
                    table.ForeignKey(
                        name: "FK_LenderLogos_Lenders_LenderId",
                        column: x => x.LenderId,
                        principalTable: "Lenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LenderLogos");

            migrationBuilder.DropColumn(
                name: "LogoUpdatedUtc",
                table: "Lenders");
        }
    }
}
