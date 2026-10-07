using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Netcash service key administration.
/// <para>
/// This screen never displays or accepts a key value. The database holds only a secret name;
/// the key itself is written into user-secrets or Key Vault out of band. That is deliberate -
/// a service key is a live payment credential, and a screen that can show one is a screen
/// that can leak one.
/// </para>
/// </summary>
// The Netcash integration runs under AM-Pay's ISV number: platform staff only.
[Authorize(Policy = AppPolicies.PlatformOnly)]
public class ServiceKeysController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly INetcashPartnerService _partner;
    private readonly INetcashSecretStore _secrets;
    private readonly INetcashCapabilityService _capabilities;
    private readonly ILogger<ServiceKeysController> _log;

    public ServiceKeysController(
        AppDbContext db,
        ICurrentTenant tenant,
        INetcashPartnerService partner,
        INetcashSecretStore secrets,
        INetcashCapabilityService capabilities,
        ILogger<ServiceKeysController> log)
    {
        _db = db;
        _tenant = tenant;
        _partner = partner;
        _secrets = secrets;
        _capabilities = capabilities;
        _log = log;
    }

    public async Task<IActionResult> Index(Guid? tenantId)
    {
        ViewData["Title"] = "Netcash service keys";
        await _tenant.LoadAsync();

        var target = _tenant.IsPlatformUser ? tenantId ?? _tenant.TenantId : _tenant.TenantId;

        if (target is null)
        {
            TempData["Info"] = "Select a customer account first.";
            return RedirectToAction("Index", "Tenants");
        }

        _tenant.EnsureCanAccess(target.Value);

        var tenant = await _db.Tenants
            .Include(t => t.ServiceKeys)
            .FirstOrDefaultAsync(t => t.Id == target);

        if (tenant is null) return NotFound();

        // Show whether each secret actually resolves, without ever showing the value.
        var resolved = new Dictionary<Guid, bool>();
        foreach (var key in tenant.ServiceKeys)
            resolved[key.Id] = await _secrets.GetServiceKeyAsync(key.SecretName) is not null;

        ViewBag.Resolved = resolved;

        // What each key actually means for the operator: not just "loaded", but whether the
        // service can be used right now and what is missing if it cannot.
        ViewBag.Capabilities = await _capabilities.GetAllAsync(tenant.Id);

        return View(tenant);
    }

    /// <summary>
    /// Validates the configured keys against Netcash.
    /// <para>
    /// Netcash locks the merchant account after three failed attempts inside ten minutes, so
    /// this is an explicit button rather than something that runs on a timer or on page load.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Validate(Guid tenantId)
    {
        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(tenantId);

        var tenant = await _db.Tenants
            .Include(t => t.ServiceKeys)
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant is null) return NotFound();

        if (string.IsNullOrWhiteSpace(tenant.NetcashAccountNumber))
        {
            TempData["Error"] = "Set the Netcash account number on this customer before validating.";
            return RedirectToAction(nameof(Index), new { tenantId });
        }

        var keys = new Dictionary<NetcashServiceId, string>();
        foreach (var k in tenant.ServiceKeys.Where(k => k.IsActive))
        {
            var value = await _secrets.GetServiceKeyAsync(k.SecretName);
            if (value is not null) keys[k.ServiceId] = value;
        }

        if (keys.Count == 0)
        {
            TempData["Error"] =
                "No service key values resolved from the secret store. Load them with " +
                "dotnet user-secrets (development) or Key Vault (production) first.";
            return RedirectToAction(nameof(Index), new { tenantId });
        }

        var result = await _partner.ValidateServiceKeysAsync(tenant.NetcashAccountNumber, keys);

        if (!result.Success || result.Data is null)
        {
            TempData["Error"] = result.Message ?? "Validation failed.";
            _log.LogWarning("Service key validation failed for {Account}: {Code} {Message}",
                tenant.NetcashAccountNumber, result.Code, result.Message);

            return RedirectToAction(nameof(Index), new { tenantId });
        }

        foreach (var validation in result.Data)
        {
            var key = tenant.ServiceKeys.FirstOrDefault(k => k.ServiceId == validation.ServiceId);
            if (key is null) continue;

            key.Status = validation.Status;
            key.LastValidatedUtc = DateTime.UtcNow;
            key.LastValidationMessage = validation.Message;
        }

        await _db.SaveChangesAsync();

        var ok = result.Data.Count(v => v.Status == ServiceKeyStatus.Validated);
        TempData["Success"] = $"{ok} of {result.Data.Count} service key(s) validated.";

        return RedirectToAction(nameof(Index), new { tenantId });
    }

    /// <summary>Adds a slot for a service key that was not pre-created.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(Guid tenantId, NetcashServiceId serviceId)
    {
        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(tenantId);

        var exists = await _db.TenantServiceKeys
            .AnyAsync(k => k.TenantId == tenantId && k.ServiceId == serviceId);

        if (exists)
        {
            TempData["Error"] = "That service already has a key slot.";
            return RedirectToAction(nameof(Index), new { tenantId });
        }

        _db.TenantServiceKeys.Add(new Domain.Entities.TenantServiceKey
        {
            TenantId = tenantId,
            ServiceId = serviceId,
            SecretName = $"Netcash:ServiceKeys:{tenantId}:{(int)serviceId}",
            Status = ServiceKeyStatus.Unverified
        });

        await _db.SaveChangesAsync();

        TempData["Success"] = $"Slot added for {serviceId}. Load the key value into the secret store.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }
}
