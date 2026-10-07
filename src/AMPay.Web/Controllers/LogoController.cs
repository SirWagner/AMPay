using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Serves a lender's logo to anyone signed in to that lender (and to platform staff) - every
/// role sees it in the sidebar, so it cannot sit behind Branding's administrator-only policy.
/// </summary>
[Authorize]
public class LogoController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public LogoController(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet("/logo/{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(id);

        var logo = await _db.TenantLogos.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == id);
        if (logo is null) return NotFound();

        // The URL carries ?v={updated ticks}, so a new logo is a new URL and this can cache.
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(logo.Data, logo.ContentType);
    }
}
