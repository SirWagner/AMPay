using AMPay.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The client's own account on the self-service portal: give access (the client gets the link
/// by SMS), send the latest figures now, preview what the client sees, or withdraw access.
/// </summary>
public partial class ClientsController
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PortalAccess(Guid id, [FromServices] ClientPortalPublisher publisher)
    {
        var client = await LoadClientAsync(id, c => c.Addresses);
        if (client is null) return NotFound();

        var error = await publisher.GrantAccessAsync(client, User.Identity?.Name);
        if (error is null)
            TempData["Success"] = "Online account opened. The client has been sent the link by SMS - they sign in with a code to their cell number and confirm their ID number once.";
        else
            TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PortalPublish(Guid id, [FromServices] ClientPortalPublisher publisher)
    {
        var client = await LoadClientAsync(id, c => c.Addresses);
        if (client is null) return NotFound();

        var error = await publisher.PublishAsync(client, force: true);
        if (error is null) TempData["Success"] = "The client's online account now shows today's figures.";
        else TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PortalRevoke(Guid id, [FromServices] ClientPortalPublisher publisher)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        var error = await publisher.RevokeAccessAsync(client, User.Identity?.Name);
        if (error is null) TempData["Success"] = "Online account access withdrawn. The client can no longer see their loans online.";
        else TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Exactly what the client sees, for the lender to check before or after giving access.</summary>
    [HttpGet]
    public async Task<IActionResult> PortalPreview(Guid id, [FromServices] ClientPortalPublisher publisher)
    {
        var client = await LoadClientAsync(id, c => c.Addresses);
        if (client is null) return NotFound();

        ViewData["Title"] = "What the client sees online";
        ViewBag.Client = client;
        ViewBag.Link = AccountLinkFor(publisher, client.TenantId);
        return View(await publisher.BuildAsync(client));
    }

    private string? AccountLinkFor(ClientPortalPublisher publisher, Guid tenantId)
    {
        var tenant = _db.Tenants.AsNoTracking().FirstOrDefault(t => t.Id == tenantId);
        return tenant is null ? null : publisher.AccountLink(tenant);
    }
}
