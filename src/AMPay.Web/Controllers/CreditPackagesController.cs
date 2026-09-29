using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Controllers;

/// <summary>
/// The price lists every loan is quoted against, per customer account.
/// <para>
/// Pricing is set by the AM-Pay platform administrator and nobody else. A customer's own
/// administrator can see their packages - they need to know what they are selling - but
/// cannot change them: the rates are regulated, and a customer repricing its own book is a
/// compliance exposure for the platform whose ISV number the collections run under.
/// </para>
/// <para>
/// Repricing affects only loans quoted afterwards. Every loan copies the rates it was
/// priced at, so an agreement a client has already signed is never restated.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.CanApproveCredit)]
public class CreditPackagesController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly NcaCreditLimits _limits;
    private readonly ILogger<CreditPackagesController> _log;

    public CreditPackagesController(
        AppDbContext db,
        ICurrentTenant tenant,
        IOptions<NcaCreditLimits> limits,
        ILogger<CreditPackagesController> log)
    {
        _db = db;
        _tenant = tenant;
        _limits = limits.Value;
        _log = log;
    }

    // ---------------------------------------------------------------- read

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Credit packages";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to see its credit packages.";
            return RedirectToAction("Index", "Tenants");
        }

        var packages = await _db.CreditPackages.AsNoTracking()
            .Where(p => p.TenantId == _tenant.TenantId)
            .OrderBy(p => p.Tier).ThenBy(p => p.Name)
            .ToListAsync();

        ViewBag.Limits = _limits;
        ViewBag.CanManage = User.IsInRole(AppRoles.SuperAdmin);
        ViewBag.CustomerName = _tenant.TenantName;
        ViewBag.MissingTiers = CreditPackageDefaults
            .MissingFor(_tenant.TenantId.Value, packages.Select(p => (p.Tier, p.Name)))
            .Select(p => p.Name)
            .ToList();

        return View(packages);
    }

    // ---------------------------------------------------------------- create

    [HttpGet]
    [Authorize(Policy = AppPolicies.PlatformOnly)]
    public async Task<IActionResult> Create()
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction("Index", "Tenants");

        ViewData["Title"] = "New credit package";
        ViewBag.Limits = _limits;
        ViewBag.CustomerName = _tenant.TenantName;

        // Start from the Regular terms rather than a blank form: every field then holds a
        // lawful value, and the operator only changes what makes this package different.
        var template = CreditPackageDefaults.For(_tenant.TenantId.Value)[0];

        var model = ToModel(template);
        model.Id = Guid.Empty;
        model.Name = "";
        model.Description = null;

        return View("Edit", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.PlatformOnly)]
    public async Task<IActionResult> Create(CreditPackageEditModel model)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction("Index", "Tenants");

        var tenantId = _tenant.TenantId.Value;

        ViewData["Title"] = "New credit package";
        ViewBag.Limits = _limits;
        ViewBag.CustomerName = _tenant.TenantName;

        await ValidateAsync(model, tenantId, excludingId: null);
        if (!ModelState.IsValid) return View("Edit", model);

        var package = new CreditPackage { TenantId = tenantId };
        Apply(model, package);

        _db.CreditPackages.Add(package);
        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Credit package {Package} created for tenant {TenantId}: {Rate:P2}/month, R{Fee} service fee.",
            package.Name, tenantId, package.MonthlyInterestRate, package.MonthlyServiceFee);

        TempData["Success"] = package.IsActive
            ? $"{package.Name} created and available for new loans."
            : $"{package.Name} created. It is withdrawn, so it will not be offered until you make it available.";

        return RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------- edit

    [HttpGet]
    [Authorize(Policy = AppPolicies.PlatformOnly)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var package = await LoadAsync(id);
        if (package is null) return NotFound();

        ViewData["Title"] = $"Edit {package.Name}";
        ViewBag.Limits = _limits;
        ViewBag.CustomerName = _tenant.TenantName;

        return View(ToModel(package));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.PlatformOnly)]
    public async Task<IActionResult> Edit(CreditPackageEditModel model)
    {
        var package = await LoadAsync(model.Id);
        if (package is null) return NotFound();

        ViewData["Title"] = $"Edit {package.Name}";
        ViewBag.Limits = _limits;
        ViewBag.CustomerName = _tenant.TenantName;

        await ValidateAsync(model, package.TenantId, excludingId: package.Id);
        if (!ModelState.IsValid) return View(model);

        Apply(model, package);
        package.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Credit package {Package} repriced for tenant {TenantId}: {Rate:P2}/month, R{Fee} service fee.",
            package.Name, package.TenantId, package.MonthlyInterestRate, package.MonthlyServiceFee);

        TempData["Success"] =
            $"{package.Name} updated. The new pricing applies to loans quoted from now on; " +
            "existing loans keep the rates they were agreed at.";

        return RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------- standard set

    /// <summary>
    /// Adds whichever standard packages this customer is missing. Never touches a package
    /// that already exists, so a repriced package keeps its pricing.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.PlatformOnly)]
    public async Task<IActionResult> AddStandard()
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction("Index", "Tenants");

        var tenantId = _tenant.TenantId.Value;

        var existing = await _db.CreditPackages
            .Where(p => p.TenantId == tenantId)
            .Select(p => new { p.Tier, p.Name })
            .ToListAsync();

        var missing = CreditPackageDefaults.MissingFor(tenantId, existing.Select(e => (e.Tier, e.Name)));

        if (missing.Count > 0)
        {
            _db.CreditPackages.AddRange(missing);
            await _db.SaveChangesAsync();

            _log.LogInformation("Added {Count} standard credit package(s) for tenant {TenantId}.",
                missing.Count, tenantId);
        }

        TempData["Success"] = missing.Count == 0
            ? "This customer already has a package at every tier."
            : $"Added {string.Join(", ", missing.Select(p => p.Name))}. Review the pricing before quoting.";

        return RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Everything a package must satisfy, for create and edit alike.
    /// <para>
    /// The attribute ranges on the model carry today's statutory ceilings as a first line
    /// of defence. The configured limits checked here are the real authority, because they
    /// change when the gazette does.
    /// </para>
    /// </summary>
    private async Task ValidateAsync(CreditPackageEditModel model, Guid tenantId, Guid? excludingId)
    {
        var name = model.Name?.Trim() ?? "";

        if (name.Length > 0)
        {
            // Names are what an operator picks from when quoting. The unique index is
            // case-insensitive, so this check is too - otherwise it passes here and fails
            // as a database error on save.
            var taken = await _db.CreditPackages.AnyAsync(p =>
                p.TenantId == tenantId &&
                p.Name.ToLower() == name.ToLower() &&
                (excludingId == null || p.Id != excludingId));

            if (taken)
                ModelState.AddModelError(nameof(model.Name),
                    $"This customer already has a package called “{name}”. Choose another name.");
        }

        if (!Enum.IsDefined(model.Tier))
            ModelState.AddModelError(nameof(model.Tier), "Choose a tier.");

        if (model.MinLoanAmount > model.MaxLoanAmount)
            ModelState.AddModelError(nameof(model.MaxLoanAmount),
                "The maximum loan must be at least the minimum.");

        if (model.MinTermMonths > model.MaxTermMonths)
            ModelState.AddModelError(nameof(model.MaxTermMonths),
                "The maximum term must be at least the minimum.");

        if (model.MonthlyInterestPercent / 100m > _limits.MonthlyInterestCeiling)
            ModelState.AddModelError(nameof(model.MonthlyInterestPercent),
                $"Interest cannot exceed the statutory maximum of {_limits.MonthlyInterestCeiling:P2} per month.");

        if (model.MonthlyServiceFee > _limits.MonthlyServiceFeeCeiling)
            ModelState.AddModelError(nameof(model.MonthlyServiceFee),
                $"The service fee cannot exceed the statutory maximum of R {_limits.MonthlyServiceFeeCeiling:N2}.");

        if (model.CreditLifePercent / 100m > _limits.CreditLifeCeilingRate)
            ModelState.AddModelError(nameof(model.CreditLifePercent),
                $"Credit life cannot exceed {_limits.CreditLifeCeilingRate:P2} of the balance per month.");
    }

    private static void Apply(CreditPackageEditModel model, CreditPackage package)
    {
        package.Tier = model.Tier;
        package.Name = model.Name.Trim();
        package.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        package.MonthlyInterestRate = Math.Round(model.MonthlyInterestPercent / 100m, 6);
        package.MonthlyServiceFee = model.MonthlyServiceFee;
        package.InitiationFeeRate = Math.Round(model.InitiationFeePercent / 100m, 6);
        package.CreditLifeRate = Math.Round(model.CreditLifePercent / 100m, 6);
        package.MinLoanAmount = model.MinLoanAmount;
        package.MaxLoanAmount = model.MaxLoanAmount;
        package.MinTermMonths = model.MinTermMonths;
        package.MaxTermMonths = model.MaxTermMonths;
        package.IsActive = model.IsActive;
    }

    private static CreditPackageEditModel ToModel(CreditPackage p) => new()
    {
        Id = p.Id,
        Tier = p.Tier,
        Name = p.Name,
        Description = p.Description,
        MonthlyInterestPercent = p.MonthlyInterestRate * 100m,
        MonthlyServiceFee = p.MonthlyServiceFee,
        InitiationFeePercent = p.InitiationFeeRate * 100m,
        CreditLifePercent = p.CreditLifeRate * 100m,
        MinLoanAmount = p.MinLoanAmount,
        MaxLoanAmount = p.MaxLoanAmount,
        MinTermMonths = p.MinTermMonths,
        MaxTermMonths = p.MaxTermMonths,
        IsActive = p.IsActive
    };

    private async Task<CreditPackage?> LoadAsync(Guid id)
    {
        var package = await _db.CreditPackages.FirstOrDefaultAsync(p => p.Id == id);
        if (package is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(package.TenantId);

        return package;
    }
}
