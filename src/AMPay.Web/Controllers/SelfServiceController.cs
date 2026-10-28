using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// A lender's public self-service link: the address to send clients to and the button to
/// put on the lender's own website. Generating the link also registers the lender with the
/// portal, along with its packages for estimates.
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class SelfServiceController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IPortalApi _portal;
    private readonly PortalSync _sync;
    private readonly ILogger<SelfServiceController> _log;

    // One portal push per business at a time. Two overlapping "create link" presses each issue a
    // code; whichever the portal stores last need not be the one AM-Pay saved last, and the link
    // shown would then not open. (One app instance, so an in-process lock is enough.)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Busy = new();

    public SelfServiceController(AppDbContext db, ICurrentTenant tenant, IPortalApi portal, PortalSync sync, ILogger<SelfServiceController> log)
    {
        _db = db;
        _tenant = tenant;
        _portal = portal;
        _sync = sync;
        _log = log;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Self-service link";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to see its self-service link.";
            return RedirectToAction("Index", "Tenants");
        }

        var t = await _db.Tenants.AsNoTracking().FirstAsync(x => x.Id == _tenant.TenantId);
        ViewBag.Link = _portal.LinkFor(t.SelfServiceCode);
        ViewBag.PortalConfigured = _portal.IsConfigured;
        return View(t);
    }

    /// <summary>Issues a code, or replaces it - the old link stops working at once.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate()
    {
        var t = await CurrentTenantAsync();
        if (t is null) return RedirectToAction(nameof(Index));

        var gate = Busy.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            TempData["Info"] = "Your link is already being created - give it a moment, then refresh this page.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            return await GenerateLockedAsync(t);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IActionResult> GenerateLockedAsync(Domain.Entities.Tenant t)
    {
        var replacing = t.SelfServiceCode is not null;
        string code;
        do code = PortalApi.NewLenderCode();
        while (await _db.Tenants.AnyAsync(x => x.SelfServiceCode == code));

        // Portal first: if it cannot take the new code, the old link keeps working.
        if (!await SyncAsync(t, code, alreadyLocked: true)) return RedirectToAction(nameof(Index));

        t.SelfServiceCode = code;
        t.PortalSyncedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Self-service code {Action} for tenant {TenantId}.", replacing ? "replaced" : "issued", t.Id);
        TempData["Success"] = replacing
            ? "New link created. The old link no longer works - update the button on your website."
            : "Your self-service link is ready. Send it to clients or put the button on your website.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(string? advisorWhatsApp)
    {
        var t = await CurrentTenantAsync();
        if (t is null) return RedirectToAction(nameof(Index));

        var normalised = string.IsNullOrWhiteSpace(advisorWhatsApp) ? null : PortalApi.NormaliseMobile(advisorWhatsApp);
        if (!string.IsNullOrWhiteSpace(advisorWhatsApp) && normalised is null)
        {
            TempData["Error"] = "Enter the WhatsApp number as a South African cell number, like 082 123 4567.";
            return RedirectToAction(nameof(Index));
        }

        t.AdvisorWhatsApp = normalised is null ? null : PortalApi.DisplayMobile(normalised);
        await _db.SaveChangesAsync();

        if (t.SelfServiceCode is not null && await SyncAsync(t, t.SelfServiceCode))
        {
            t.PortalSyncedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        TempData["Success"] ??= "Saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Sends the lender's current details and packages to the portal again.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync()
    {
        var t = await CurrentTenantAsync();
        if (t?.SelfServiceCode is null) return RedirectToAction(nameof(Index));

        if (await SyncAsync(t, t.SelfServiceCode))
        {
            t.PortalSyncedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "The portal now has your current details and packages.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> SyncAsync(Domain.Entities.Tenant t, string code, bool alreadyLocked = false)
    {
        // Generate already holds the gate. Anything else waits its turn rather than pushing an
        // old code over the new one a link creation is about to save.
        var gate = Busy.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        var mine = false;
        if (!alreadyLocked)
        {
            if (!await gate.WaitAsync(0))
            {
                TempData["Info"] = "The portal is busy with your link right now - try again in a minute.";
                return false;
            }
            mine = true;
        }
        try
        {
            await _sync.PushAsync(t, code);
            return true;
        }
        catch (PortalUnavailableException ex)
        {
            TempData["Error"] = ex.Message;
            return false;
        }
        finally
        {
            if (mine) gate.Release();
        }
    }

    private async Task<Domain.Entities.Tenant?> CurrentTenantAsync()
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return null;
        return await _db.Tenants.FirstAsync(x => x.Id == _tenant.TenantId);
    }
}
