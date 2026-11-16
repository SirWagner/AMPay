using System.Security.Claims;
using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The lender's activity feed: every payment, unpaid debit, loan decision, signed agreement,
/// debit-order change and new client, newest first - plus what is waiting on the self-service portal.
/// </summary>
[Authorize]
public class NotificationsController : Controller
{
    public static readonly (string Kind, string Label)[] Kinds =
    {
        ("Payment", "Payments"), ("Unpaid", "Unpaid debits"), ("Loan", "Loans"), ("Agreement", "Agreements"),
        ("DebitOrder", "Debit orders"), ("PayNow", "Pay Now"), ("Client", "New clients")
    };

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IPortalApi _portal;

    public NotificationsController(AppDbContext db, ICurrentTenant tenant, IPortalApi portal)
    {
        _db = db;
        _tenant = tenant;
        _portal = portal;
    }

    public async Task<IActionResult> Index(string? kind, bool attention = false, int days = 30)
    {
        ViewData["Title"] = "Notifications";
        await _tenant.LoadAsync();
        if (_tenant.TenantId is not { } tenantId)
        {
            TempData["Info"] = "Choose a customer account to see its notifications.";
            return RedirectToAction("Index", "Tenants");
        }

        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var q = _db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.CreatedUtc >= since);
        if (!string.IsNullOrEmpty(kind)) q = q.Where(n => n.Kind == kind);
        if (attention) q = q.Where(n => n.Tone == NotificationTone.Attention);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var seen = await _db.NotificationsSeen.FirstOrDefaultAsync(s => s.UserId == userId && s.TenantId == tenantId);

        var model = new NotificationsPage
        {
            Items = await q.OrderByDescending(n => n.CreatedUtc).Take(300).ToListAsync(),
            Counts = await _db.Notifications.AsNoTracking()
                .Where(n => n.TenantId == tenantId && n.CreatedUtc >= since)
                .GroupBy(n => n.Kind).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count),
            LastSeenUtc = seen?.LastSeenUtc,
            Kind = kind, Attention = attention, Days = days
        };

        // Waiting on the portal: read live, as the Applications page does. Best effort.
        if (_portal.IsConfigured && await _db.Tenants.AnyAsync(t => t.Id == tenantId && t.SelfServiceCode != null))
        {
            try
            {
                model.PortalApplications = (await _portal.ApplicationsAsync(tenantId)).Count(a => a.Status == Domain.Portal.PortalApplicationStatus.Submitted);
                model.PortalCallbacks = (await _portal.CallbacksAsync(tenantId)).Count;
                model.PortalLoginRequests = (await _portal.LinkRequestsAsync(tenantId)).Count;
            }
            catch (PortalUnavailableException) { model.PortalError = true; }
        }

        // Opening the page marks everything up to now as seen.
        if (!string.IsNullOrEmpty(userId))
        {
            if (seen is null) _db.NotificationsSeen.Add(new NotificationSeen { UserId = userId, TenantId = tenantId, LastSeenUtc = DateTime.UtcNow });
            else seen.LastSeenUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return View(model);
    }
}

public class NotificationsPage
{
    public List<Notification> Items { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
    public DateTime? LastSeenUtc { get; set; }
    public string? Kind { get; set; }
    public bool Attention { get; set; }
    public int Days { get; set; }
    public int PortalApplications { get; set; }
    public int PortalCallbacks { get; set; }
    public int PortalLoginRequests { get; set; }
    public bool PortalError { get; set; }
}
