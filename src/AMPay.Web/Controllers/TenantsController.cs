using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Customer (tenant) management - the ISV layer.
/// <para>
/// Platform-only. These are the businesses trading under the AM-Pay Netcash ISV number, and
/// creating one is effectively onboarding a new Netcash merchant.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.PlatformOnly)]
public class TenantsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public TenantsController(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Customers";
        await _tenant.LoadAsync();

        var tenants = await _db.Tenants
            .AsNoTracking()
            .OrderByDescending(t => t.IsPlatformOwner)
            .ThenBy(t => t.Name)
            .Select(t => new
            {
                Tenant = t,
                Clients = t.Clients.Count,
                Keys = t.ServiceKeys.Count(k => k.IsActive)
            })
            .ToListAsync();

        ViewBag.Rows = tenants.Select(x => (x.Tenant, x.Clients, x.Keys)).ToList();
        ViewBag.SelectedTenantId = _tenant.TenantId;

        return View();
    }

    /// <summary>Platform users work inside one customer at a time; this is that switch.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(Guid id)
    {
        await _tenant.LoadAsync();
        await _tenant.SelectTenantAsync(id);

        TempData["Success"] = $"Now working in {_tenant.TenantName}.";
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult Create()
    {
        ViewData["Title"] = "New customer";
        return View(new TenantModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TenantModel model)
    {
        ViewData["Title"] = "New customer";

        if (!string.IsNullOrWhiteSpace(model.NetcashAccountNumber) &&
            await _db.Tenants.AnyAsync(t => t.NetcashAccountNumber == model.NetcashAccountNumber))
        {
            ModelState.AddModelError(nameof(model.NetcashAccountNumber),
                "Another customer is already using that Netcash account number.");
        }

        if (!ModelState.IsValid) return View(model);

        var tenant = new Tenant
        {
            Name = model.Name.Trim(),
            TradingName = model.TradingName?.Trim(),
            RegistrationNumber = model.RegistrationNumber?.Trim(),
            NcrNumber = model.NcrNumber?.Trim(),
            NetcashAccountNumber = model.NetcashAccountNumber?.Trim(),
            ContactEmail = model.ContactEmail?.Trim(),
            ContactNumber = model.ContactNumber?.Trim(),
            VatNumber = model.VatNumber?.Trim(),
            PhysicalAddress = model.PhysicalAddress?.Trim(),
            PostalAddress = model.PostalAddress?.Trim(),
            CreditLifeUnderwriter = model.CreditLifeUnderwriter?.Trim(),
            CreditLifeAdministrator = model.CreditLifeAdministrator?.Trim(),
            Status = model.Status
        };

        _db.Tenants.Add(tenant);

        // Pre-create the service key slots so the operator has somewhere to put the keys
        // when Netcash issues them. The secret name is fixed now; the value comes later.
        foreach (var service in new[]
                 {
                     NetcashServiceId.Account,
                     NetcashServiceId.DebitOrders,
                     NetcashServiceId.PayNow
                 })
        {
            _db.TenantServiceKeys.Add(new TenantServiceKey
            {
                TenantId = tenant.Id,
                ServiceId = service,
                SecretName = $"Netcash:ServiceKeys:{tenant.Id}:{(int)service}",
                Status = ServiceKeyStatus.Unverified
            });
        }

        // A customer with no credit package cannot raise a single loan, so it starts with
        // the standard three. Its administrator reprices them from Credit packages.
        _db.CreditPackages.AddRange(CreditPackageDefaults.For(tenant.Id));

        await _db.SaveChangesAsync();

        TempData["Success"] = $"{tenant.Name} created. Load its Netcash service keys next.";
        return RedirectToAction("Index", "ServiceKeys", new { tenantId = tenant.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        ViewData["Title"] = "Customer";

        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant is null) return NotFound();

        return View(new TenantModel
        {
            Id = tenant.Id,
            Name = tenant.Name,
            TradingName = tenant.TradingName,
            RegistrationNumber = tenant.RegistrationNumber,
            NcrNumber = tenant.NcrNumber,
            NetcashAccountNumber = tenant.NetcashAccountNumber,
            ContactEmail = tenant.ContactEmail,
            ContactNumber = tenant.ContactNumber,
            VatNumber = tenant.VatNumber,
            PhysicalAddress = tenant.PhysicalAddress,
            PostalAddress = tenant.PostalAddress,
            CreditLifeUnderwriter = tenant.CreditLifeUnderwriter,
            CreditLifeAdministrator = tenant.CreditLifeAdministrator,
            Status = tenant.Status
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(TenantModel model)
    {
        ViewData["Title"] = "Customer";
        if (!ModelState.IsValid) return View(model);

        var tenant = await _db.Tenants.FindAsync(model.Id);
        if (tenant is null) return NotFound();

        tenant.Name = model.Name.Trim();
        tenant.TradingName = model.TradingName?.Trim();
        tenant.RegistrationNumber = model.RegistrationNumber?.Trim();
        tenant.NcrNumber = model.NcrNumber?.Trim();
        tenant.NetcashAccountNumber = model.NetcashAccountNumber?.Trim();
        tenant.ContactEmail = model.ContactEmail?.Trim();
        tenant.ContactNumber = model.ContactNumber?.Trim();
        tenant.VatNumber = model.VatNumber?.Trim();
        tenant.PhysicalAddress = model.PhysicalAddress?.Trim();
        tenant.PostalAddress = model.PostalAddress?.Trim();
        tenant.CreditLifeUnderwriter = model.CreditLifeUnderwriter?.Trim();
        tenant.CreditLifeAdministrator = model.CreditLifeAdministrator?.Trim();
        tenant.Status = model.Status;
        tenant.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["Success"] = "Customer updated.";
        return RedirectToAction(nameof(Index));
    }
}
