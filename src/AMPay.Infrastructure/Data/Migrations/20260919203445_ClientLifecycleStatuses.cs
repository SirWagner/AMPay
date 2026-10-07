using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AMPay.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Remaps existing client rows onto the expanded lifecycle.
    /// <para>
    /// No schema changes: ClientStatus keeps its original four numbers and gains four more.
    /// But the meaning of Active (1) narrowed - it used to mean "capture complete, mandates
    /// may be created", and now means "holding a disbursed loan". Every client already
    /// carrying that value was activated under the old meaning, so they belong in
    /// Onboarded (6) unless they genuinely have a disbursed loan against them.
    /// </para>
    /// <para>
    /// Without this, every historical client would read as an active borrower and the loan
    /// book figures on the dashboard would be wrong from the first page load.
    /// </para>
    /// </summary>
    public partial class ClientLifecycleStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 = Active (old meaning), 6 = Onboarded, 4 = Disbursed on Loans.
            migrationBuilder.Sql(@"
                UPDATE c
                SET    c.Status = 6
                FROM   Clients c
                WHERE  c.Status = 1
                AND    NOT EXISTS (
                           SELECT 1 FROM Loans l
                           WHERE  l.ClientId = c.Id AND l.Status = 4);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Onboarded did not exist before this migration; fold it back into Active.
            migrationBuilder.Sql("UPDATE Clients SET Status = 1 WHERE Status = 6;");
        }
    }
}
