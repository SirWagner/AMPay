using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Sends one lender's public face - details, packages and logo - to the self-service portal.
/// One place, so the Self-service link, Credit packages and Branding screens all send the same thing.
/// </summary>
public class PortalSync
{
    private readonly AppDbContext _db;
    private readonly IPortalApi _portal;

    public PortalSync(AppDbContext db, IPortalApi portal)
    {
        _db = db;
        _portal = portal;
    }

    public bool IsConfigured => _portal.IsConfigured;

    /// <summary>Pushes the lender under <paramref name="code"/>. Throws <see cref="PortalUnavailableException"/>.</summary>
    public async Task PushAsync(Tenant tenant, string code, CancellationToken ct = default)
    {
        var packages = await _db.CreditPackages.AsNoTracking().Where(p => p.TenantId == tenant.Id).ToListAsync(ct);
        var logo = await _db.TenantLogos.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenant.Id, ct);

        var body = PortalClient.SyncFor(tenant, packages) with
        {
            LogoContentType = logo?.ContentType,
            LogoBase64 = logo is null ? null : Convert.ToBase64String(logo.Data)
        };

        await _portal.PutLenderAsync(code, body, ct);
    }

    /// <summary>
    /// Pushes a lender that already has a link, after a change to its packages or logo. Best
    /// effort: returns a message to show when the portal could not be reached, else null.
    /// </summary>
    public async Task<string?> RefreshAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant?.SelfServiceCode is null || !_portal.IsConfigured) return null;

        try
        {
            await PushAsync(tenant, tenant.SelfServiceCode, ct);
            tenant.PortalSyncedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }
        catch (PortalUnavailableException ex)
        {
            return "Saved. The self-service portal could not be updated just now (" + ex.Message +
                   ") - use Self-service link → Send current packages to the portal.";
        }
    }
}
