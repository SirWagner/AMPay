using System.ComponentModel.DataAnnotations;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// A lender's own lending figures: the capital set aside to lend, and how long clients may
/// cancel after payout. Both feed the dashboard.
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class LendingSettingsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IMemoryCache _cache;

    public LendingSettingsController(AppDbContext db, ICurrentTenant tenant, IMemoryCache cache)
    {
        _db = db;
        _tenant = tenant;
        _cache = cache;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Lending settings";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to manage its lending settings.";
            return RedirectToAction("Index", "Tenants");
        }

        var t = await _db.Tenants.AsNoTracking().FirstAsync(x => x.Id == _tenant.TenantId);
        ViewBag.TenantName = t.TradingName ?? t.Name;
        return View(new LendingSettingsModel
        {
            LendingCapital = t.LendingCapital,
            CoolingOffBusinessDays = t.CoolingOffBusinessDays
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LendingSettingsModel model)
    {
        ViewData["Title"] = "Lending settings";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction("Index", "Tenants");

        var t = await _db.Tenants.FirstAsync(x => x.Id == _tenant.TenantId);
        if (!ModelState.IsValid)
        {
            ViewBag.TenantName = t.TradingName ?? t.Name;
            return View(model);
        }

        t.LendingCapital = model.LendingCapital;
        t.CoolingOffBusinessDays = model.CoolingOffBusinessDays;
        t.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Show the change straight away rather than after the dashboard's minute is up.
        _cache.Remove(LenderDashboardService.CacheKey(t.Id));

        TempData["Success"] = "Lending settings saved.";
        return RedirectToAction(nameof(Index));
    }
}

public class LendingSettingsModel
{
    /// <summary>Empty means "not set": the dashboard then shows no capital figure rather than a guess.</summary>
    [Display(Name = "Capital set aside for lending (R)")]
    [Range(0, 1_000_000_000, ErrorMessage = "Enter an amount of R 0 or more.")]
    public decimal? LendingCapital { get; set; }

    [Display(Name = "Cooling-off period (business days)")]
    [Range(0, 30, ErrorMessage = "Enter between 0 and 30 business days.")]
    public int CoolingOffBusinessDays { get; set; } = 5;
}
