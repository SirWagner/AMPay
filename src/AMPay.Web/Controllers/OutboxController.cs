using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Every SMS and email AM-Pay has sent - or, while no provider is connected, would have
/// sent. Administrators only: messages carry clients' contact details.
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class OutboxController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IMessageSender _sender;

    public OutboxController(AppDbContext db, ICurrentTenant tenant, IMessageSender sender)
    {
        _db = db;
        _tenant = tenant;
        _sender = sender;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Messages sent";
        await _tenant.LoadAsync();

        var query = _db.OutboundMessages.AsNoTracking();

        // A platform user with no customer selected sees everything, including platform messages.
        if (_tenant.TenantId is { } tenantId)
            query = query.Where(m => m.TenantId == tenantId);
        else if (!_tenant.IsPlatformUser)
            return Forbid();

        var messages = await query
            .OrderByDescending(m => m.CreatedUtc)
            .Take(200)
            .ToListAsync();

        ViewBag.Stubbed = _sender.IsStubbed;
        ViewBag.CustomerName = _tenant.TenantName;
        return View(messages);
    }
}
