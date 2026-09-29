using System.Globalization;
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

        // A platform user with no customer selected sees the whole book; everyone else is
        // pinned to their own tenant. Building one predicate here keeps the two views from
        // drifting apart as the dashboard grows.
        var tenantId = _tenant.TenantId;
        var crossTenant = _tenant.IsPlatformUser && tenantId is null;

        if (crossTenant)
            model.CustomerCount = await _db.Tenants.CountAsync(t => !t.IsPlatformOwner);
        else if (tenantId is null)
            return View(model);

        var clients = _db.Clients.AsNoTracking()
            .Where(c => crossTenant || c.TenantId == tenantId);
        var mandates = _db.Mandates.AsNoTracking()
            .Where(m => crossTenant || m.TenantId == tenantId);
        var loans = _db.Loans.AsNoTracking()
            .Where(l => crossTenant || l.TenantId == tenantId);
        var payNow = _db.PayNowTransactions.AsNoTracking()
            .Where(p => crossTenant || p.TenantId == tenantId);

        // ---- Client lifecycle ----

        var byStatus = await clients
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        int CountOf(ClientStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        model.ClientCount = byStatus.Sum(x => x.Count);
        model.CapturedClients = CountOf(ClientStatus.Captured);
        model.AwaitingVerification = CountOf(ClientStatus.PendingVerification);
        model.OnboardedClients = CountOf(ClientStatus.Onboarded);
        model.ActiveClients = CountOf(ClientStatus.Active);

        model.Pipeline = ClientStatusInfo.Pipeline
            .Select(s => new ChartSlice(ClientStatusInfo.Label(s), CountOf(s)))
            .ToList();

        // ---- Mandates ----

        var mandatesByStatus = await mandates
            .GroupBy(m => m.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        int MandatesOf(MandateStatus s) =>
            mandatesByStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        model.AuthenticatedMandates = MandatesOf(MandateStatus.Authenticated);
        model.PendingMandates = MandatesOf(MandateStatus.SubmittedToBank) +
                                MandatesOf(MandateStatus.AwaitingDebtorAuthentication);
        model.RejectedMandates = MandatesOf(MandateStatus.Rejected);

        model.MandateBreakdown = mandatesByStatus
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Select(x => new ChartSlice(Humanise(x.Status.ToString()), x.Count))
            .ToList();

        model.MonthlyCollectionValue = await mandates
            .Where(m => m.Status == MandateStatus.Authenticated)
            .SumAsync(m => (decimal?)m.CollectionAmount) ?? 0m;

        model.PayNowCollected = await payNow
            .Where(p => p.Status == PaymentStatus.Complete)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        // ---- Loan book ----

        model.LoansInApproval = await loans.CountAsync(l => l.Status == LoanStatus.PendingApproval);
        model.DisbursedLoans = await loans.CountAsync(l => l.Status == LoanStatus.Disbursed);
        model.LoanBookPrincipal = await loans
            .Where(l => l.Status == LoanStatus.Disbursed)
            .SumAsync(l => (decimal?)l.Principal) ?? 0m;
        model.LoanBookOutstanding = await loans
            .Where(l => l.Status == LoanStatus.Disbursed)
            .SumAsync(l => (decimal?)l.CapitalisedAmount) ?? 0m;

        // Projected to an anonymous type first: ChartSlice has an optional constructor
        // parameter, which EF cannot put in an expression tree.
        var byPackage = await loans
            .Where(l => l.Status == LoanStatus.Disbursed || l.Status == LoanStatus.Approved)
            .GroupBy(l => l.CreditPackage!.Name)
            .Select(g => new { Package = g.Key, Advanced = g.Sum(l => l.Principal) })
            .ToListAsync();

        model.LoanBookByPackage = byPackage
            .Select(x => new ChartSlice(x.Package, x.Advanced))
            .ToList();

        // ---- Captured per month, last twelve ----

        var since = DateTime.UtcNow.AddMonths(-11).Date;
        var firstOfMonth = new DateTime(since.Year, since.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var monthly = await clients
            .Where(c => c.CreatedUtc >= firstOfMonth)
            .GroupBy(c => new { c.CreatedUtc.Year, c.CreatedUtc.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();

        // Months with no intake still need a point, or the line lies about the trend.
        model.ClientsByMonth = Enumerable.Range(0, 12)
            .Select(offset =>
            {
                var month = firstOfMonth.AddMonths(offset);
                var hit = monthly.FirstOrDefault(m => m.Year == month.Year && m.Month == month.Month);
                return new ChartSlice(
                    month.ToString("MMM yy", CultureInfo.GetCultureInfo("en-ZA")),
                    hit?.Count ?? 0);
            })
            .ToList();

        // ---- Recent activity ----

        model.RecentClients = await clients
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

        model.RecentMandates = await mandates
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

    /// <summary>Turns a PascalCase enum name into something a person would read.</summary>
    private static string Humanise(string pascal) =>
        System.Text.RegularExpressions.Regex.Replace(pascal, "(?<!^)([A-Z])", " $1");

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewData["Title"] = "Something went wrong";
        return View();
    }
}
