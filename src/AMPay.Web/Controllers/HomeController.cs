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

        // Grouped by what the status means, not listed one Netcash state at a time. The
        // operator's question is how much will collect, how much is stuck, and how much has
        // failed - not which of ten lifecycle states each mandate happens to be in.
        int Total(params MandateStatus[] statuses) => statuses.Sum(MandatesOf);

        model.MandateHealth = new List<MandateHealthSlice>
        {
            new("Authenticated", "Collectable",
                Total(MandateStatus.Authenticated), MandateHealthTone.Good),
            new("In progress", "With the bank or the debtor",
                Total(MandateStatus.Draft, MandateStatus.PendingMasterfile, MandateStatus.SubmittedToBank,
                      MandateStatus.AwaitingDebtorAuthentication, MandateStatus.Amended),
                MandateHealthTone.Warning),
            new("Failed or rejected", "Will not collect",
                Total(MandateStatus.Rejected, MandateStatus.Failed, MandateStatus.Expired),
                MandateHealthTone.Critical),
            new("Cancelled", "Closed",
                Total(MandateStatus.Cancelled), MandateHealthTone.Neutral)
        };

        model.DocumentsAwaitingReview = await _db.ClientDocuments.AsNoTracking()
            .Where(d => d.ReviewStatus == DocumentReviewStatus.Pending)
            .Where(d => crossTenant || d.Client!.TenantId == tenantId)
            .CountAsync();

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

        // By tier rather than package name: names are editable per customer, the tier is
        // not, so the chart means the same thing across every customer on the platform.
        // Projected to an anonymous type first - ChartSlice has an optional constructor
        // parameter, which EF cannot put in an expression tree.
        var byTier = await loans
            .Where(l => l.Status == LoanStatus.Disbursed || l.Status == LoanStatus.Approved)
            .GroupBy(l => l.CreditPackage!.Tier)
            .Select(g => new { Tier = g.Key, Advanced = g.Sum(l => l.Principal) })
            .ToListAsync();

        // Every tier present, in tier order: a Premium bar of zero is information, and a
        // missing one reads as a fault.
        model.LoanBookByPackage = Enum.GetValues<CreditTier>()
            .OrderBy(t => t)
            .Select(t => new ChartSlice(t.ToString(), byTier.FirstOrDefault(x => x.Tier == t)?.Advanced ?? 0m))
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

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewData["Title"] = "Something went wrong";
        return View();
    }
}
