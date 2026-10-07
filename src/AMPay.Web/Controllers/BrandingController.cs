using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// A lender's logo: shown in their Loan Flow and on their self-service portal, so both feel
/// like the lender's own system.
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class BrandingController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly PortalSync _sync;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<BrandingController> _log;

    public BrandingController(AppDbContext db, ICurrentTenant tenant, PortalSync sync, UserManager<ApplicationUser> users, ILogger<BrandingController> log)
    {
        _db = db;
        _tenant = tenant;
        _sync = sync;
        _users = users;
        _log = log;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Branding";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to manage its branding.";
            return RedirectToAction("Index", "Tenants");
        }

        ViewBag.LogoVersion = await _db.TenantLogos.AsNoTracking()
            .Where(l => l.TenantId == _tenant.TenantId)
            .Select(l => (DateTime?)l.UpdatedUtc)
            .FirstOrDefaultAsync();
        ViewBag.TenantId = _tenant.TenantId;
        ViewBag.TenantName = _tenant.TenantName;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));
        var tenantId = _tenant.TenantId.Value;

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose your logo file.";
            return RedirectToAction(nameof(Index));
        }

        if (file.Length > TenantLogo.MaxBytes)
        {
            TempData["Error"] = $"The logo is {file.Length / 1024} KB. Use a file of {TenantLogo.MaxBytes / 1024} KB or less - a PNG about 400 pixels wide is plenty.";
            return RedirectToAction(nameof(Index));
        }

        byte[] bytes;
        await using (var s = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        // The bytes decide, not the file name: a renamed SVG or script is refused here.
        var contentType = TenantLogo.SniffContentType(bytes);
        if (contentType is null)
        {
            TempData["Error"] = "Upload the logo as a PNG, JPG or WebP image.";
            return RedirectToAction(nameof(Index));
        }

        var logo = await _db.TenantLogos.FirstOrDefaultAsync(l => l.TenantId == tenantId);
        if (logo is null)
        {
            logo = new TenantLogo { TenantId = tenantId };
            _db.TenantLogos.Add(logo);
        }

        logo.ContentType = contentType;
        logo.Data = bytes;
        logo.UpdatedUtc = DateTime.UtcNow;
        logo.UpdatedByUserId = _users.GetUserId(User);
        await _db.SaveChangesAsync();

        _log.LogInformation("Logo updated for tenant {TenantId} by {User}.", tenantId, User.Identity?.Name);

        TempData["Success"] = "Logo saved. It now shows in your Loan Flow and on your self-service page.";
        if (await _sync.RefreshAsync(tenantId) is { } warning) TempData["Info"] = warning;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove()
    {
        await _tenant.LoadAsync();
        if (_tenant.TenantId is null) return RedirectToAction(nameof(Index));

        var logo = await _db.TenantLogos.FirstOrDefaultAsync(l => l.TenantId == _tenant.TenantId);
        if (logo is not null)
        {
            _db.TenantLogos.Remove(logo);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Logo removed.";
            if (await _sync.RefreshAsync(_tenant.TenantId.Value) is { } warning) TempData["Info"] = warning;
        }
        return RedirectToAction(nameof(Index));
    }

}
