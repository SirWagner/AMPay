using AMPay.Domain.Credit;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The statutory fee and interest caps every quote is checked against. Platform admin only:
/// one set of caps applies to every lender. A change is a new row from a date, never an edit,
/// so loans priced earlier stay checkable against the caps of their day.
/// </summary>
[Authorize(Policy = AppPolicies.PlatformOnly)]
public class RegulatoryCapsController : Controller
{
    private readonly AppDbContext _db;
    private readonly RegulatoryCapsProvider _caps;
    private readonly UserManager<ApplicationUser> _users;

    public RegulatoryCapsController(AppDbContext db, RegulatoryCapsProvider caps, UserManager<ApplicationUser> users)
    {
        _db = db;
        _caps = caps;
        _users = users;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Regulatory caps";
        ViewBag.Today = Sast.DateOf(DateTime.UtcNow);
        ViewBag.Current = await _caps.ForDateAsync((DateTime)ViewBag.Today);
        ViewBag.History = await _db.RegulatoryCaps.AsNoTracking()
            .OrderBy(c => c.Type).ThenByDescending(c => c.EffectiveFrom)
            .ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(CapType type, decimal? value, DateTime? effectiveFrom, string? source)
    {
        if (value is null || value < 0)
        {
            TempData["Error"] = "Enter the new value (zero or more).";
            return RedirectToAction(nameof(Index));
        }

        // Rates are typed as percentages on screen and stored as fractions.
        var stored = RegulatoryCaps.IsRate(type) ? value.Value / 100m : value.Value;
        if (RegulatoryCaps.IsRate(type) && stored > 1m)
        {
            TempData["Error"] = "A rate above 100% is not a cap.";
            return RedirectToAction(nameof(Index));
        }

        var from = (effectiveFrom ?? Sast.DateOf(DateTime.UtcNow)).Date;
        await _caps.SetAsync(type, stored, from, source, _users.GetUserId(User));

        TempData["Success"] = $"{RegulatoryCaps.Label(type)} set from {from:d MMM yyyy}. Quotes from that date use it.";
        return RedirectToAction(nameof(Index));
    }
}
