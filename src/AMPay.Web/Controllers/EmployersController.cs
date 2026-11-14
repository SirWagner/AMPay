using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The lender's employer list. Capturers add to it without coming here - an employer typed on
/// the Employment step is saved to the list by itself - so this page is for tidying: the HR
/// number to phone, the pay day, and hiding duplicates.
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class EmployersController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public EmployersController(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index(string? q)
    {
        ViewData["Title"] = "Employers";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is not { } tenantId) return NoTenant();

        var query = _db.Employers.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e => e.Name.Contains(term) || (e.PayPoint != null && e.PayPoint.Contains(term)));
        }

        var employers = await query.OrderBy(e => e.Name).ThenBy(e => e.PayPoint).Take(500).ToListAsync();

        var counts = await _db.ClientEmployments.AsNoTracking()
            .Where(e => e.EmployerId != null && e.Client!.TenantId == tenantId)
            .GroupBy(e => e.EmployerId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        ViewBag.Query = q;
        return View(employers.Select(e => new EmployerRow
        {
            Employer = e,
            Clients = counts.GetValueOrDefault(e.Id)
        }).ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New employer";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return NoTenant();

        return View("Edit", new EmployerEditModel());
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var e = await _db.Employers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(e.TenantId);

        ViewData["Title"] = e.Name;
        return View(new EmployerEditModel
        {
            Id = e.Id,
            Name = e.Name,
            PayPoint = e.PayPoint,
            Telephone = e.Telephone,
            ContactPerson = e.ContactPerson,
            EmailAddress = e.EmailAddress,
            Address = e.Address,
            PayFrequency = e.PayFrequency,
            SalaryDay = e.SalaryDay,
            IsVerified = e.IsVerified,
            IsActive = e.IsActive
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EmployerEditModel model)
    {
        ViewData["Title"] = model.Id is null ? "New employer" : model.Name;
        await _tenant.LoadAsync();

        Employer e;
        if (model.Id is { } id)
        {
            var existing = await _db.Employers.FirstOrDefaultAsync(x => x.Id == id);
            if (existing is null) return NotFound();
            _tenant.EnsureCanAccess(existing.TenantId);
            e = existing;
        }
        else
        {
            if (_tenant.TenantId is not { } tenantId) return NoTenant();
            e = new Employer { TenantId = tenantId };
        }

        var name = model.Name?.Trim() ?? "";
        var payPoint = string.IsNullOrWhiteSpace(model.PayPoint) ? null : model.PayPoint.Trim();
        var lower = name.ToLower();
        var twins = await _db.Employers.AsNoTracking()
            .Where(x => x.TenantId == e.TenantId && x.Id != e.Id && x.Name.ToLower() == lower)
            .Select(x => x.PayPoint)
            .ToListAsync();
        if (twins.Any(p => string.Equals(p?.Trim() ?? "", payPoint ?? "", StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(model.Name), "That employer and pay point is already on the list.");

        if (!ModelState.IsValid) return View(model);

        e.Name = name;
        e.PayPoint = payPoint;
        e.Telephone = model.Telephone?.Trim();
        e.ContactPerson = model.ContactPerson?.Trim();
        e.EmailAddress = model.EmailAddress?.Trim();
        e.Address = model.Address?.Trim();
        e.PayFrequency = model.PayFrequency;
        e.SalaryDay = model.SalaryDay;
        e.IsVerified = model.IsVerified;
        e.IsActive = model.IsActive;
        e.UpdatedUtc = DateTime.UtcNow;

        if (model.Id is null) _db.Employers.Add(e);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{e.DisplayName} saved.";
        return RedirectToAction(nameof(Index));
    }

    private IActionResult NoTenant()
    {
        TempData["Info"] = "Choose a customer account to manage its employers.";
        return RedirectToAction("Index", "Tenants");
    }
}
