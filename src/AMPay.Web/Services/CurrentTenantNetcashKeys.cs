using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Hands live Netcash clients the signed-in business's key: its active slot's secret first, then
/// the key stored under its Netcash account number. Null when either is missing or deactivated.
/// </summary>
public class CurrentTenantNetcashKeys : INetcashKeyProvider
{
    private readonly ICurrentTenant _tenant;
    private readonly AppDbContext _db;
    private readonly INetcashSecretStore _secrets;

    public CurrentTenantNetcashKeys(ICurrentTenant tenant, AppDbContext db, INetcashSecretStore secrets)
    {
        _tenant = tenant;
        _db = db;
        _secrets = secrets;
    }

    public async Task<string?> KeyAsync(NetcashServiceId serviceId, CancellationToken ct = default)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is not { } tenantId) return null;

        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.NetcashAccountNumber,
                Slot = t.ServiceKeys.Where(k => k.ServiceId == serviceId).Select(k => new { k.SecretName, k.IsActive }).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (tenant is null || tenant.Slot is { IsActive: false }) return null;

        return await _secrets.ResolveServiceKeyAsync(tenant.Slot?.SecretName ?? "", tenant.NetcashAccountNumber, serviceId, ct);
    }
}
