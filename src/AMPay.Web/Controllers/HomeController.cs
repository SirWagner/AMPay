using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public HomeController(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Dashboard";
        await _tenant.LoadAsync();

        var model = new DashboardViewModel
        {
            IsPlatformUser = _tenant.IsPlatformUser,
            TenantName = _tenant.TenantName
        };

        if (_tenant.IsPlatformUser && _tenant.TenantId is null)
        {
            // Platform view: across every customer.
            model.CustomerCount = await _db.Tenants.CountAsync(t => !t.IsPlatformOwner);
            model.ClientCount = await _db.Clients.CountAsync();
            model.AuthenticatedMandates = await _db.Mandates
                .CountAsync(m => m.Status == MandateStatus.Authenticated);
            model.PendingMandates = await _db.Mandates.CountAsync(m =>
                m.Status == MandateStatus.SubmittedToBank ||
                m.Status == MandateStatus.AwaitingDebtorAuthentication);
            model.PayNowCollected = await _db.PayNowTransactions
                .Where(p => p.Status == PaymentStatus.Complete)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;
            return View(model);
        }

        var tenantId = _tenant.TenantId;
        if (tenantId is null) return View(model);

        model.ClientCount = await _db.Clients.CountAsync(c => c.TenantId == tenantId);
        model.AuthenticatedMandates = await _db.Mandates
            .CountAsync(m => m.TenantId == tenantId && m.Status == MandateStatus.Authenticated);
        model.PendingMandates = await _db.Mandates.CountAsync(m =>
            m.TenantId == tenantId &&
            (m.Status == MandateStatus.SubmittedToBank ||
             m.Status == MandateStatus.AwaitingDebtorAuthentication));
        model.RejectedMandates = await _db.Mandates
            .CountAsync(m => m.TenantId == tenantId && m.Status == MandateStatus.Rejected);
        model.PayNowCollected = await _db.PayNowTransactions
            .Where(p => p.TenantId == tenantId && p.Status == PaymentStatus.Complete)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        model.MonthlyCollectionValue = await _db.Mandates
            .Where(m => m.TenantId == tenantId && m.Status == MandateStatus.Authenticated)
            .SumAsync(m => (decimal?)m.CollectionAmount) ?? 0m;

        model.RecentClients = await _db.Clients
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.CreatedUtc)
            .Take(6)
            .Select(c => new DashboardViewModel.ClientRow
            {
                Id = c.Id,
                ClientNumber = c.ClientNumber,
                Name = c.FirstName + " " + c.Surname,
                Status = c.Status,
                CreatedUtc = c.CreatedUtc
            })
            .ToListAsync();

        model.RecentMandates = await _db.Mandates
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(6)
            .Select(m => new DashboardViewModel.MandateRow
            {
                Id = m.Id,
                AccountReference = m.AccountReference,
                ClientName = m.Client!.FirstName + " " + m.Client.Surname,
                Amount = m.CollectionAmount,
                Status = m.Status,
                MandateType = m.MandateType
            })
            .ToListAsync();

        return View(model);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewData["Title"] = "Something went wrong";
        return View();
    }
}
