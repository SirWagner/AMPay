using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Portal.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccountLinkedUtc",
                table: "Applicants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientAccountId",
                table: "Applicants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedIdAttempts",
                table: "Applicants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ClientAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmpayClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    IdNumberHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatementJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientAccounts_Lenders_LenderId",
                        column: x => x.LenderId,
                        principalTable: "Lenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAccounts_LenderId_AmpayClientId",
                table: "ClientAccounts",
                columns: new[] { "LenderId", "AmpayClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientAccounts_LenderId_Mobile",
                table: "ClientAccounts",
                columns: new[] { "LenderId", "Mobile" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientAccounts");

            migrationBuilder.DropColumn(
                name: "AccountLinkedUtc",
                table: "Applicants");

            migrationBuilder.DropColumn(
                name: "ClientAccountId",
                table: "Applicants");

            migrationBuilder.DropColumn(
                name: "FailedIdAttempts",
                table: "Applicants");
        }
    }
}
