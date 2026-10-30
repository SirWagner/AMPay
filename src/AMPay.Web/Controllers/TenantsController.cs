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
using System.Security.Claims;

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
    private readonly PortalSync _sync;

    public TenantsController(AppDbContext db, ICurrentTenant tenant, PortalSync sync)
    {
        _db = db;
        _tenant = tenant;
        _sync = sync;
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
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Create(TenantModel model, IFormFile? logo)
    {
        ViewData["Title"] = "New customer";

        if (!string.IsNullOrWhiteSpace(model.NetcashAccountNumber) &&
            await _db.Tenants.AnyAsync(t => t.NetcashAccountNumber == model.NetcashAccountNumber))
        {
            ModelState.AddModelError(nameof(model.NetcashAccountNumber),
                "Another customer is already using that Netcash account number.");
        }

        var (logoBytes, logoType, logoError) = await ReadLogoAsync(logo);
        if (logoError is not null) ModelState.AddModelError("logo", logoError);

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

        if (logoBytes is not null)
            _db.TenantLogos.Add(new TenantLogo
            {
                TenantId = tenant.Id, ContentType = logoType!, Data = logoBytes,
                UpdatedUtc = DateTime.UtcNow, UpdatedByUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
            });

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

        ViewBag.HasLogo = await _db.TenantLogos.AnyAsync(l => l.TenantId == id);

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
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Edit(TenantModel model, IFormFile? logo)
    {
        ViewData["Title"] = "Customer";

        var (logoBytes, logoType, logoError) = await ReadLogoAsync(logo);
        if (logoError is not null) ModelState.AddModelError("logo", logoError);

        if (!ModelState.IsValid)
        {
            ViewBag.HasLogo = await _db.TenantLogos.AnyAsync(l => l.TenantId == model.Id);
            return View(model);
        }

        var tenant = await _db.Tenants.FindAsync(model.Id);
        if (tenant is null) return NotFound();

        if (logoBytes is not null)
        {
            var existing = await _db.TenantLogos.FirstOrDefaultAsync(l => l.TenantId == tenant.Id);
            if (existing is null)
            {
                existing = new TenantLogo { TenantId = tenant.Id };
                _db.TenantLogos.Add(existing);
            }
            existing.ContentType = logoType!;
            existing.Data = logoBytes;
            existing.UpdatedUtc = DateTime.UtcNow;
            existing.UpdatedByUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        }

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

        TempData["Success"] = logoBytes is null ? "Customer updated." : "Customer updated, with the new logo.";
        // A business with a self-service link shows its logo there too.
        if (logoBytes is not null && await _sync.RefreshAsync(tenant.Id) is { } warning) TempData["Info"] = warning;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Reads an optional logo upload. The bytes decide what it is, not the file name, so a renamed
    /// SVG or script is refused. Returns nulls when nothing was uploaded.
    /// </summary>
    private static async Task<(byte[]? Bytes, string? ContentType, string? Error)> ReadLogoAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) return (null, null, null);
        if (file.Length > TenantLogo.MaxBytes)
            return (null, null, $"The logo is {file.Length / 1024} KB. Use a file of {TenantLogo.MaxBytes / 1024} KB or less - a PNG about 400 pixels wide is plenty.");

        await using var s = file.OpenReadStream();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var type = TenantLogo.SniffContentType(bytes);
        return type is null
            ? (null, null, "Upload the logo as a PNG, JPG or WebP image.")
            : (bytes, type, null);
    }
}
