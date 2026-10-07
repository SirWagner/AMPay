using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

public static class ClientNumbers
{
    /// <summary>
    /// Next sequential client number for the tenant.
    /// <para>
    /// Racy under concurrent capture - two operators starting at the same moment can land on
    /// the same number. The unique index on (TenantId, ClientNumber) catches it, and the
    /// operator can change the number. Replace with a per-tenant sequence if that becomes a
    /// routine collision rather than a rare one.
    /// </para>
    /// </summary>
    public static async Task<string> NextAsync(AppDbContext db, Guid tenantId)
    {
        var numbers = await db.Clients
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.ClientNumber)
            .ToListAsync();

        var highest = numbers
            .Select(n => int.TryParse(n, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        return (highest + 1).ToString("D4");
    }
}
