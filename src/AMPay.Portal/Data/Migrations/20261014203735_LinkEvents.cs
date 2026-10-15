using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Portal.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkEvents_Lenders_LenderId",
                        column: x => x.LenderId,
                        principalTable: "Lenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkEvents_LenderId_OccurredUtc",
                table: "LinkEvents",
                columns: new[] { "LenderId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkEvents");
        }
    }
}
