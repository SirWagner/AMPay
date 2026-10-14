using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using AMPay.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using static AMPay.Web.Models.LenderDashboardViewModel;

namespace AMPay.Web.Services;

/// <summary>
/// Works out a lender's dashboard: how their money is doing, what needs them today, and
/// whether they are growing and compliant. Every figure is a set-based aggregate scoped to
/// one tenant; nothing loads a whole table. The result is cached per tenant for a minute.
/// <para>
/// What AM-Pay does not record yet - debit results, balances, arrears - is left out rather
/// than estimated. See docs/lender-dashboard-data-map.md.
/// </para>
/// </summary>
public class LenderDashboardService
{
    /// <summary>Until a per-lender setting exists (proposal M3), the NCA's usual five business days.</summary>
    public const int CoolingOffBusinessDays = 5;

    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public LenderDashboardService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<LenderDashboardViewModel> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        (await _cache.GetOrCreateAsync($"lender-dashboard:{tenantId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return BuildAsync(tenantId, DateTime.UtcNow, ct);
        }))!;

    /// <summary>The uncached calculation, with the clock passed in so it can be tested at any moment.</summary>
    public async Task<LenderDashboardViewModel> BuildAsync(Guid tenantId, DateTime utcNow, CancellationToken ct = default)
    {
        var today = Sast.DateOf(utcNow);
        var monthStart = Sast.MonthStart(today);
        var nextMonth = monthStart.AddMonths(1);
        var lastMonth = monthStart.AddMonths(-1);

        // Instants are UTC; month boundaries are SAST midnights.
        var monthStartUtc = Sast.StartUtc(monthStart);
        var nextMonthUtc = Sast.StartUtc(nextMonth);
        var lastMonthUtc = Sast.StartUtc(lastMonth);

        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.TradingName, t.SelfServiceCode })
            .FirstAsync(ct);

        var loans = _db.Loans.AsNoTracking().Where(l => l.TenantId == tenantId);

        // ---- this month's instalments, by due date (one grouped query) ----

        var byDueDate = await (
                from s in _db.LoanScheduleEntries.AsNoTracking()
                join l in loans on s.LoanId equals l.Id
                where l.Status == LoanStatus.Disbursed && s.DueDate >= monthStart && s.DueDate < nextMonth
                select new
                {
                    s.DueDate,
                    s.InstalmentTotal,
                    s.InterestPortion,
                    s.ServiceFee,
                    s.CreditLifePremium,
                    Ready = l.Mandate != null && l.Mandate.Status == MandateStatus.Authenticated ? 1 : 0
                })
            .GroupBy(x => x.DueDate)
            .Select(g => new
            {
                Date = g.Key,
                Debits = g.Count(),
                Amount = g.Sum(x => x.InstalmentTotal),
                Ready = g.Sum(x => x.Ready),
                Interest = g.Sum(x => x.InterestPortion),
                ServiceFees = g.Sum(x => x.ServiceFee),
                CreditLife = g.Sum(x => x.CreditLifePremium)
            })
            .ToListAsync(ct);

        var upcoming = byDueDate
            .Where(r => r.Date >= today)
            .OrderBy(r => r.Date)
            .Select(r => new CollectionRun(r.Date, r.Debits, r.Amount, r.Ready))
            .ToList();

        // ---- disbursements: this month and last ----

        // Plain counts and sums: they translate the same on SQL Server and in tests.
        var thisMonthLoans = loans.Where(l => l.DisbursedUtc >= monthStartUtc && l.DisbursedUtc < nextMonthUtc);
        var countThisMonth = await thisMonthLoans.CountAsync(ct);
        var principalThisMonth = await thisMonthLoans.SumAsync(l => (decimal?)l.Principal, ct) ?? 0m;
        var initiationThisMonth = await thisMonthLoans.SumAsync(l => (decimal?)l.InitiationFee, ct) ?? 0m;

        var disbursedLastMonth = await loans
            .CountAsync(l => l.DisbursedUtc >= lastMonthUtc && l.DisbursedUtc < monthStartUtc, ct);

        var daysElapsed = (today - monthStart).Days + 1;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);

        // ---- Pay Now this month ----

        var payNowThisMonth = _db.PayNowTransactions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Status == PaymentStatus.Complete &&
                        p.CompletedUtc >= monthStartUtc && p.CompletedUtc < nextMonthUtc);
        var payNowCount = await payNowThisMonth.CountAsync(ct);
        var payNowAmount = await payNowThisMonth.SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        // ---- action queues ----

        var awaiting = _db.Mandates.AsNoTracking()
            .Where(m => m.TenantId == tenantId &&
                        (m.Status == MandateStatus.SubmittedToBank || m.Status == MandateStatus.AwaitingDebtorAuthentication));
        var awaitingCount = await awaiting.CountAsync(ct);
        var oldestAwaiting = await awaiting.MinAsync(m => (DateTime?)(m.SubmittedUtc ?? m.CreatedUtc), ct);

        var documentsToReview = await _db.ClientDocuments.AsNoTracking()
            .CountAsync(d => d.Client!.TenantId == tenantId && d.ReviewStatus == DocumentReviewStatus.Pending, ct);

        var pending = loans.Where(l => l.Status == LoanStatus.PendingApproval);
        var pendingCount = await pending.CountAsync(ct);
        var pendingRequested = await pending.SumAsync(l => (decimal?)l.Principal, ct) ?? 0m;

        var unsignedAgreements = await _db.LoanContracts.AsNoTracking()
            .CountAsync(c => c.TenantId == tenantId &&
                             (c.Status == ContractStatus.Sent || c.Status == ContractStatus.Viewed), ct);

        // In the window while fewer than N business days have passed since the payout date.
        var coolingCutoffUtc = Sast.StartUtc(Sast.BusinessDaysBefore(today, CoolingOffBusinessDays).AddDays(1));
        var coolingOff = await loans
            .CountAsync(l => l.Status == LoanStatus.Disbursed && l.DisbursedUtc >= coolingCutoffUtc, ct);

        // ---- lent per SAST month, last six ----

        var sixMonthsStart = monthStart.AddMonths(-5);
        var sixMonthsStartUtc = Sast.StartUtc(sixMonthsStart);
        var lentRows = await loans
            .Where(l => l.DisbursedUtc >= sixMonthsStartUtc && l.DisbursedUtc < nextMonthUtc)
            .Select(l => new { Local = l.DisbursedUtc!.Value.AddHours(2), l.Principal })
            .GroupBy(x => new { x.Local.Year, x.Local.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Lent = g.Sum(x => x.Principal), Count = g.Count() })
            .ToListAsync(ct);

        var lentByMonth = Enumerable.Range(0, 6)
            .Select(i =>
            {
                var m = sixMonthsStart.AddMonths(i);
                var hit = lentRows.FirstOrDefault(r => r.Year == m.Year && r.Month == m.Month);
                return new MonthBar(m.ToString("MMM"), hit?.Lent ?? 0m, hit?.Count ?? 0, m == monthStart);
            })
            .ToList();

        // ---- growth ----

        var decisions = await loans
            .Where(l => l.DecidedUtc >= monthStartUtc && l.DecidedUtc < nextMonthUtc)
            .GroupBy(l => l.Status == LoanStatus.Declined)
            .Select(g => new { Declined = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // A loan is a repeat when the same client had an earlier loan paid out.
        var repeatLoans = await thisMonthLoans
            .CountAsync(l => _db.Loans.Any(o => o.ClientId == l.ClientId && o.Id != l.Id &&
                                                o.DisbursedUtc != null && o.DisbursedUtc < l.DisbursedUtc), ct);

        const string portalSource = "Self-service portal";
        var portalImported = await _db.Clients.AsNoTracking()
            .CountAsync(c => c.TenantId == tenantId && c.OtherDetails != null && c.OtherDetails.Source == portalSource &&
                             c.CreatedUtc >= monthStartUtc && c.CreatedUtc < nextMonthUtc, ct);
        var portalDisbursed = await loans
            .CountAsync(l => l.DisbursedUtc >= monthStartUtc && l.DisbursedUtc < nextMonthUtc &&
                             l.Client!.OtherDetails != null && l.Client.OtherDetails.Source == portalSource, ct);

        // ---- compliance, on loans being collected ----

        var active = loans.Where(l => l.Status == LoanStatus.Disbursed);
        var activeCount = await active.CountAsync(ct);
        var withAffordability = await active.CountAsync(l => l.Assessments.Any(), ct);
        var withCreditCheck = await active.CountAsync(l => l.Client!.CreditEnquiries.Any(), ct);
        var missingDocuments = await active.CountAsync(l =>
            !l.Client!.Documents.Any(d => d.DocumentType == DocumentType.IdDocument && d.ReviewStatus == DocumentReviewStatus.Approved) ||
            !l.Client.Documents.Any(d => d.DocumentType == DocumentType.Payslip && d.ReviewStatus == DocumentReviewStatus.Approved), ct);

        // ---- onboarding pipeline ----

        var clientStatuses = await _db.Clients.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var hasPackages = await _db.CreditPackages.AnyAsync(p => p.TenantId == tenantId && p.IsActive, ct);
        var hasAnyLoans = await loans.AnyAsync(ct);

        return new LenderDashboardViewModel
        {
            TenantId = tenantId,
            TenantName = tenant.TradingName ?? tenant.Name,
            TodaySast = today,
            GeneratedUtc = utcNow,
            HasActivePackages = hasPackages,
            HasSelfServiceLink = tenant.SelfServiceCode is not null,
            HasAnyLoans = hasAnyLoans,

            Collections = new CollectionsSection
            {
                Expected = byDueDate.Sum(r => r.Amount),
                ExpectedDebits = byDueDate.Sum(r => r.Debits),
                StillScheduled = upcoming.Sum(r => r.Amount),
                StillScheduledDebits = upcoming.Sum(r => r.Debits),
                PayNowCollected = payNowAmount,
                PayNowCount = payNowCount,
                NextRun = upcoming.FirstOrDefault(),
                LargestRun = upcoming.OrderByDescending(r => r.Amount).ThenBy(r => r.Date).FirstOrDefault()
            },

            Kpis = new KpiSection
            {
                ActiveLoans = activeCount,
                DisbursedThisMonth = countThisMonth,
                DisbursedPrincipalThisMonth = principalThisMonth,
                OnPaceFor = (int)Math.Round((double)countThisMonth / daysElapsed * daysInMonth),
                DisbursedLastMonth = disbursedLastMonth,
                InitiationFees = initiationThisMonth,
                Interest = byDueDate.Sum(r => r.Interest),
                ServiceFees = byDueDate.Sum(r => r.ServiceFees),
                CreditLife = byDueDate.Sum(r => r.CreditLife)
            },

            Actions = new List<ActionCard>
            {
                new("failed", "Failed collections", null,
                    "Debit results are not recorded yet", "", null, Highlight: true),
                new("mandates", "Mandates awaiting client", awaitingCount,
                    oldestAwaiting is { } oldest ? $"Oldest waiting {Waited(oldest, utcNow)}" : "None waiting",
                    "Chase", "/Mandates?status=AwaitingDebtorAuthentication"),
                new("documents", "Documents to review", documentsToReview,
                    documentsToReview == 0 ? "All reviewed" : "ID documents and payslips", "Review", "/Documents"),
                new("approval", "Loans awaiting approval", pendingCount,
                    pendingCount == 0 ? "Nothing to decide" : $"R {pendingRequested:N0} requested",
                    "Decide", "/Loans?status=PendingApproval"),
                new("unsigned", "Agreements not signed", unsignedAgreements,
                    unsignedAgreements == 0 ? "None outstanding" : "Sent to clients, not yet signed",
                    "Resend", "/Contracts?filter=unsigned"),
                new("coolingoff", "In cooling-off period", coolingOff,
                    $"Paid out in the last {CoolingOffBusinessDays} business days", "View", "/Loans?status=Disbursed")
            },

            LentByMonth = lentByMonth,
            UpcomingRuns = upcoming,

            Growth = new GrowthSection
            {
                Approved = decisions.Where(d => !d.Declined).Sum(d => d.Count),
                Declined = decisions.Where(d => d.Declined).Sum(d => d.Count),
                NewClientLoans = countThisMonth - repeatLoans,
                RepeatClientLoans = repeatLoans,
                PortalImported = portalImported,
                PortalDisbursed = portalDisbursed
            },

            Compliance = new ComplianceSection
            {
                ActiveLoans = activeCount,
                WithAffordability = withAffordability,
                WithCreditCheck = withCreditCheck,
                MissingDocuments = missingDocuments
            },

            Pipeline = ClientStatusInfo.Pipeline
                .Select(s => new PipelineStage(ClientStatusInfo.Label(s),
                    clientStatuses.FirstOrDefault(x => x.Status == s)?.Count ?? 0, s.ToString()))
                .ToList()
        };
    }

    private static string Waited(DateTime sinceUtc, DateTime nowUtc)
    {
        var days = (int)Math.Floor((nowUtc - sinceUtc).TotalDays);
        return days switch { <= 0 => "since today", 1 => "1 day", _ => $"{days} days" };
    }
}
