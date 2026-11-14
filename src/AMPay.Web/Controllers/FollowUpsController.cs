using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Everyone the lender has promised to get back to, soonest first. The notes live on each
/// client's file; this is the one list of what is still open across all of them.
/// </summary>
[Authorize(Policy = AppPolicies.CanCapture)]
public class FollowUpsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public FollowUpsController(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Follow-ups";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is not { } tenantId)
        {
            TempData["Info"] = "Select a customer account to work in.";
            return RedirectToAction("Index", "Tenants");
        }

        var rows = await _db.ClientNotes.AsNoTracking()
            .Where(n => n.Client!.TenantId == tenantId && n.FollowUpOn != null && n.FollowUpDoneUtc == null)
            .OrderBy(n => n.FollowUpOn)
            .Take(300)
            .Select(n => new FollowUpRow
            {
                NoteId = n.Id,
                ClientId = n.ClientId,
                ClientName = n.Client!.FirstName + " " + n.Client.Surname,
                ClientNumber = n.Client.ClientNumber,
                Mobile = n.Client.Addresses.Select(a => a.MobileNumber).FirstOrDefault(m => m != null && m != ""),
                Channel = n.Category,
                Body = n.Body,
                FollowUpOn = n.FollowUpOn!.Value,
                CreatedBy = n.CreatedByUserId
            })
            .ToListAsync();

        return View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Done(Guid id)
    {
        var note = await _db.ClientNotes.Include(n => n.Client).FirstOrDefaultAsync(n => n.Id == id);
        if (note?.Client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(note.Client.TenantId);

        note.FollowUpDoneUtc ??= DateTime.UtcNow;
        note.FollowUpDoneBy ??= User.Identity?.Name;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Follow-up marked done.";
        return RedirectToAction(nameof(Index));
    }
}
