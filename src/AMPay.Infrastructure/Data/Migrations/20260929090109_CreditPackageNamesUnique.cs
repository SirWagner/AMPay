using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a customer run more than one package per tier, and makes the package name the
    /// thing that must be unique instead.
    /// <para>
    /// The tier used to be unique per customer. Custom packages - two Gold price lists for
    /// two kinds of borrower - need that lifted; the tier becomes a reporting group and the
    /// name is what an operator picks from when quoting.
    /// </para>
    /// </summary>
    public partial class CreditPackageNamesUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditPackages_TenantId_Tier",
                table: "CreditPackages");

            // Until now nothing stopped two packages sharing a name - a Gold package renamed
            // "Regular", say. Creating the unique index over such data would fail this
            // migration, and with it application startup. Any duplicate after the first is
            // renamed with a numeric suffix; the first keeps its name, and nothing is deleted.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT Id,
                           ROW_NUMBER() OVER (PARTITION BY TenantId, Name ORDER BY Tier, CreatedUtc, Id) AS n
                    FROM CreditPackages)
                UPDATE p
                SET    p.Name = LEFT(p.Name, 90) + N' (' + CAST(r.n AS nvarchar(10)) + N')'
                FROM   CreditPackages p
                JOIN   ranked r ON r.Id = p.Id
                WHERE  r.n > 1;");

            migrationBuilder.CreateIndex(
                name: "IX_CreditPackages_TenantId_Name",
                table: "CreditPackages",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditPackages_TenantId_Tier",
                table: "CreditPackages",
                columns: new[] { "TenantId", "Tier" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back re-imposes one package per tier. If a customer has since been
            // given two packages at the same tier this fails - deliberately: choosing which
            // price list to discard is a business decision, not something to do silently.
            migrationBuilder.DropIndex(
                name: "IX_CreditPackages_TenantId_Name",
                table: "CreditPackages");

            migrationBuilder.DropIndex(
                name: "IX_CreditPackages_TenantId_Tier",
                table: "CreditPackages");

            migrationBuilder.CreateIndex(
                name: "IX_CreditPackages_TenantId_Tier",
                table: "CreditPackages",
                columns: new[] { "TenantId", "Tier" },
                unique: true);
        }
    }
}
