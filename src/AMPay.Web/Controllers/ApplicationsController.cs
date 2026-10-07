using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Applications clients made on the self-service portal, and their requests for a call
/// back. Read live from the portal; importing one creates (or completes) the AM-Pay client
/// and the onboarding continues as normal.
/// </summary>
[Authorize(Policy = AppPolicies.CanCapture)]
public class ApplicationsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IPortalApi _portal;
    private readonly PortalImporter _importer;
    private readonly UserManager<ApplicationUser> _users;

    public ApplicationsController(AppDbContext db, ICurrentTenant tenant, IPortalApi portal, PortalImporter importer, UserManager<ApplicationUser> users)
    {
        _db = db;
        _tenant = tenant;
        _portal = portal;
        _importer = importer;
        _users = users;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Applications";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to see its applications.";
            return RedirectToAction("Index", "Tenants");
        }

        var tenantId = _tenant.TenantId.Value;
        var model = new ApplicationsPage { PortalConfigured = _portal.IsConfigured };

        model.HasLink = await _db.Tenants.AnyAsync(t => t.Id == tenantId && t.SelfServiceCode != null);

        if (model.PortalConfigured && model.HasLink)
        {
            try
            {
                model.Applications = await _portal.ApplicationsAsync(tenantId);
                model.Callbacks = await _portal.CallbacksAsync(tenantId);
            }
            catch (PortalUnavailableException ex)
            {
                model.Error = ex.Message;
            }

            // Who is already a client here, by SA ID - so staff see a returning client at a glance.
            var ids = model.Applications.Select(a => a.IdNumber).Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
            model.ExistingClients = await _db.Clients.AsNoTracking()
                .Where(c => c.TenantId == tenantId && ids.Contains(c.IdNumber))
                .ToDictionaryAsync(c => c.IdNumber, c => (c.Id, c.ClientNumber));
        }

        return View(model);
    }

    public async Task<IActionResult> Details(Guid id)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));

        try
        {
            var app = await _portal.ApplicationAsync(_tenant.TenantId.Value, id);
            if (app is null) return NotFound();

            ViewData["Title"] = $"Application {app.Summary.Reference}";
            var idNumber = app.Summary.IdNumber ?? "";
            ViewBag.Existing = await _db.Clients.AsNoTracking()
                .Where(c => c.TenantId == _tenant.TenantId && c.IdNumber == idNumber)
                .Select(c => new { c.Id, c.ClientNumber, Name = c.FirstName + " " + c.Surname })
                .FirstOrDefaultAsync();
            return View(app);
        }
        catch (PortalUnavailableException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>Streams a document from the portal for viewing, without storing it in AM-Pay.</summary>
    public async Task<IActionResult> Document(Guid id, Guid documentId)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));

        try
        {
            var app = await _portal.ApplicationAsync(_tenant.TenantId.Value, id);
            var doc = app?.Documents.FirstOrDefault(d => d.Id == documentId);
            if (doc is null) return NotFound();

            var bytes = await _portal.DocumentAsync(_tenant.TenantId.Value, id, documentId);
            if (bytes is null) return NotFound();

            Response.Headers.CacheControl = "no-store";
            Response.Headers.ContentDisposition = $"inline; filename=\"{Path.GetFileName(doc.FileName)}\"";
            return File(bytes, doc.ContentType ?? "application/octet-stream");
        }
        catch (PortalUnavailableException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(Guid id)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));

        try
        {
            var result = await _importer.ImportAsync(_tenant.TenantId.Value, id, _users.GetUserId(User));

            TempData["Success"] =
                (result.Created
                    ? $"Client {result.Client.ClientNumber} created from the application"
                    : $"Application added to existing client {result.Client.ClientNumber}") +
                $", with {result.DocumentsCopied} document(s) sent for review. Check each step and continue the onboarding." +
                (result.Warnings.Count > 0 ? " Note: " + string.Join(" ", result.Warnings) : "");

            return RedirectToAction("Details", "Clients", new { id = result.Client.Id });
        }
        catch (PortalUnavailableException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CallbackHandled(Guid id)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));

        try
        {
            await _portal.MarkCallbackHandledAsync(_tenant.TenantId.Value, id);
            TempData["Success"] = "Call-back marked as done.";
        }
        catch (PortalUnavailableException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
}

public class ApplicationsPage
{
    public bool PortalConfigured { get; set; }
    public bool HasLink { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<PortalApplicationSummary> Applications { get; set; } = Array.Empty<PortalApplicationSummary>();
    public IReadOnlyList<PortalCallback> Callbacks { get; set; } = Array.Empty<PortalCallback>();
    public Dictionary<string, (Guid Id, string ClientNumber)> ExistingClients { get; set; } = new();
}
