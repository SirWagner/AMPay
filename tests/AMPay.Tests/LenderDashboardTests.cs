using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AMPay.Tests;

public class LenderDashboardTests
{
    // Tuesday 10 November 2026, 10:00 SAST.
    private static readonly DateTime Now = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

    // ----------------------------------------------------------------- SAST boundaries

    [Fact]
    public async Task APayoutAt2330UtcOnTheLastDay_CountsInTheNextSouthAfricanMonth()
    {
        var (db, svc, t) = await ArrangeAsync();
        AddLoan(db, t, disbursedUtc: new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc), principal: 1_000m); // 01:30 1 Nov SAST
        AddLoan(db, t, disbursedUtc: new DateTime(2026, 10, 31, 21, 30, 0, DateTimeKind.Utc), principal: 2_000m); // 23:30 31 Oct SAST
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(1, d.Kpis.DisbursedThisMonth);
        Assert.Equal(1_000m, d.Kpis.DisbursedPrincipalThisMonth);
        Assert.Equal(1, d.Kpis.DisbursedLastMonth);
    }

    [Fact]
    public async Task PayNow_IsCountedInTheSastMonth_AndOnlyWhenComplete()
    {
        var (db, svc, t) = await ArrangeAsync();
        db.PayNowTransactions.AddRange(
            PayNow(t, 500m, PaymentStatus.Complete, new DateTime(2026, 10, 31, 22, 30, 0, DateTimeKind.Utc)), // 00:30 1 Nov SAST
            PayNow(t, 700m, PaymentStatus.Complete, new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc)),  // 23:00 31 Oct SAST
            PayNow(t, 900m, PaymentStatus.Declined, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(500m, d.Collections.PayNowCollected);
        Assert.Equal(1, d.Collections.PayNowCount);
    }

    // ----------------------------------------------------------------- collections

    [Fact]
    public async Task ExpectedThisMonth_LeavesOutSettledAndWrittenOffLoans()
    {
        var (db, svc, t) = await ArrangeAsync();
        AddLoan(db, t, Now.AddDays(-30), dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 1_000m);
        AddLoan(db, t, Now.AddDays(-30), status: LoanStatus.Settled, dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 5_000m);
        AddLoan(db, t, Now.AddDays(-30), status: LoanStatus.WrittenOff, dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 7_000m);
        // Due next month: not this month's money.
        AddLoan(db, t, Now.AddDays(-30), dueDates: new[] { new DateTime(2026, 12, 1) }, instalment: 3_000m);
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(1_000m, d.Collections.Expected);
        Assert.Equal(1, d.Collections.ExpectedDebits);
        Assert.Equal(2, d.Kpis.ActiveLoans); // the two being collected; settled and written-off are not in the book
    }

    [Fact]
    public async Task UpcomingRuns_GroupByDate_FromToday_WithMandateReadiness()
    {
        var (db, svc, t) = await ArrangeAsync();
        var ready = new DebiCheckMandate { TenantId = t.Id, ClientId = Guid.NewGuid(), AccountReference = "A1", Status = MandateStatus.Authenticated };
        var waiting = new DebiCheckMandate { TenantId = t.Id, ClientId = Guid.NewGuid(), AccountReference = "A2", Status = MandateStatus.AwaitingDebtorAuthentication };
        db.Mandates.AddRange(ready, waiting);

        AddLoan(db, t, Now.AddDays(-20), dueDates: new[] { new DateTime(2026, 11, 3) }, instalment: 400m, mandate: ready);   // already past
        AddLoan(db, t, Now.AddDays(-20), dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 1_000m, mandate: ready);
        AddLoan(db, t, Now.AddDays(-20), dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 2_000m, mandate: waiting);
        AddLoan(db, t, Now.AddDays(-20), dueDates: new[] { new DateTime(2026, 11, 15) }, instalment: 500m, mandate: ready);
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(3_900m, d.Collections.Expected);
        Assert.Equal(3_500m, d.Collections.StillScheduled);
        Assert.Equal(new[] { new DateTime(2026, 11, 15), new DateTime(2026, 11, 25) }, d.UpcomingRuns.Select(r => r.Date));

        var run25 = d.UpcomingRuns.Single(r => r.Date.Day == 25);
        Assert.Equal(2, run25.Debits);
        Assert.Equal(3_000m, run25.Amount);
        Assert.Equal(1, run25.MandatesReady);
        Assert.False(run25.AllMandatesReady);

        Assert.Equal(15, d.Collections.NextRun!.Date.Day);
        Assert.Equal(25, d.Collections.LargestRun!.Date.Day);
    }

    // ----------------------------------------------------------------- income and growth

    [Fact]
    public async Task Income_IsInitiationOnLoansPaidOut_PlusChargesDueThisMonth()
    {
        var (db, svc, t) = await ArrangeAsync();
        var loan = AddLoan(db, t, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc), principal: 5_000m, initiation: 600m,
            dueDates: new[] { new DateTime(2026, 11, 25), new DateTime(2026, 12, 25) }, instalment: 2_000m);
        foreach (var s in db.LoanScheduleEntries.Local.Where(s => s.LoanId == loan.Id))
        {
            s.InterestPortion = 250m;
            s.ServiceFee = 60m;
            s.CreditLifePremium = 20m;
        }
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(600m, d.Kpis.InitiationFees);
        Assert.Equal(250m, d.Kpis.Interest);   // November's instalment only
        Assert.Equal(60m, d.Kpis.ServiceFees);
        Assert.Equal(20m, d.Kpis.CreditLife);
        Assert.Equal(930m, d.Kpis.Income);
    }

    [Fact]
    public async Task ApprovalRate_AndRepeatClients_AreWorkedOutForThisMonth()
    {
        var (db, svc, t) = await ArrangeAsync();
        var decided = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        AddLoan(db, t, null, status: LoanStatus.Approved, decidedUtc: decided);
        AddLoan(db, t, null, status: LoanStatus.Approved, decidedUtc: decided);
        AddLoan(db, t, null, status: LoanStatus.Declined, decidedUtc: decided);
        AddLoan(db, t, null, status: LoanStatus.Declined, decidedUtc: new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc)); // last month

        var returning = Guid.NewGuid();
        AddLoan(db, t, new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), clientId: returning, status: LoanStatus.Settled);
        var octDecision = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc); // approved last month, paid out this month
        AddLoan(db, t, new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), clientId: returning, decidedUtc: octDecision);
        AddLoan(db, t, new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), decidedUtc: octDecision);
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(2, d.Growth.Approved);
        Assert.Equal(1, d.Growth.Declined);
        Assert.Equal(2m / 3m, d.Growth.ApprovalRate);
        Assert.Equal(1, d.Growth.RepeatClientLoans);
        Assert.Equal(1, d.Growth.NewClientLoans);
    }

    [Fact]
    public async Task CoolingOff_CountsBusinessDays_NotCalendarDays()
    {
        var (db, svc, t) = await ArrangeAsync();
        // Now is Tuesday 10 Nov. Five business days back is Tuesday 3 Nov.
        AddLoan(db, t, new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc));  // Wed 4 Nov: inside
        AddLoan(db, t, new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc));  // Tue 3 Nov: five business days have passed
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.Equal(1, d.Actions.Single(a => a.Key == "coolingoff").Count);
    }

    [Fact]
    public void BusinessDaysBefore_SkipsWeekends()
    {
        Assert.Equal(new DateTime(2026, 11, 3), Sast.BusinessDaysBefore(new DateTime(2026, 11, 10), 5));
        Assert.Equal(new DateTime(2026, 11, 6), Sast.BusinessDaysBefore(new DateTime(2026, 11, 9), 1)); // Monday -> Friday
    }

    // ----------------------------------------------------------------- isolation and empty state

    [Fact]
    public async Task ATenantNeverSeesAnotherTenantsNumbers()
    {
        var (db, svc, a) = await ArrangeAsync();
        var b = new Tenant { Name = "Other Lender" };
        db.Tenants.Add(b);
        AddLoan(db, b, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc), principal: 9_999m, dueDates: new[] { new DateTime(2026, 11, 25) }, instalment: 4_444m);
        AddLoan(db, b, null, status: LoanStatus.PendingApproval);
        db.PayNowTransactions.Add(PayNow(b, 321m, PaymentStatus.Complete, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var d = await svc.BuildAsync(a.Id, Now);

        Assert.Equal(0, d.Kpis.DisbursedThisMonth);
        Assert.Equal(0m, d.Collections.Expected);
        Assert.Equal(0m, d.Collections.PayNowCollected);
        Assert.Equal(0, d.Actions.Single(x => x.Key == "approval").Count);
        Assert.False(d.HasAnyLoans);
    }

    [Fact]
    public async Task ABrandNewTenant_GetsEmptyStates_NotInventedNumbers()
    {
        var (_, svc, t) = await ArrangeAsync();

        var d = await svc.BuildAsync(t.Id, Now);

        Assert.False(d.HasAnyLoans);
        Assert.False(d.HasActivePackages);
        Assert.False(d.HasSelfServiceLink);
        Assert.Null(d.Collections.NextRun);
        Assert.Null(d.Growth.ApprovalRate);
        // Debit results are not recorded, so the failed-collections card has no number at all.
        Assert.Null(d.Actions.Single(a => a.Key == "failed").Count);
        Assert.Equal(6, d.LentByMonth.Count);
        Assert.True(d.LentByMonth[^1].IsCurrentMonth);
    }

    [Fact]
    public async Task TheDashboard_IsCachedPerTenantForAMinute()
    {
        var (db, svc, t) = await ArrangeAsync();
        var first = await svc.GetAsync(t.Id);

        AddLoan(db, t, null, status: LoanStatus.PendingApproval);
        await db.SaveChangesAsync();
        var second = await svc.GetAsync(t.Id);

        Assert.Same(first, second);
    }

    // ----------------------------------------------------------------- fixture

    private static async Task<(AppDbContext, LenderDashboardService, Tenant)> ArrangeAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"dash-{Guid.NewGuid()}").Options);
        var t = new Tenant { Name = "Albatross Money (Pty) Ltd", TradingName = "Albatross Money" };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return (db, new LenderDashboardService(db, new MemoryCache(new MemoryCacheOptions())), t);
    }

    private static int _loanNo;

    private static Loan AddLoan(
        AppDbContext db, Tenant t, DateTime? disbursedUtc,
        LoanStatus? status = null, decimal principal = 1_000m, decimal initiation = 0m,
        DateTime[]? dueDates = null, decimal instalment = 0m, DebiCheckMandate? mandate = null,
        Guid? clientId = null, DateTime? decidedUtc = null)
    {
        var loan = new Loan
        {
            TenantId = t.Id,
            ClientId = clientId ?? Guid.NewGuid(),
            CreditPackageId = Guid.NewGuid(),
            LoanNumber = $"LN-{Interlocked.Increment(ref _loanNo):D6}",
            Status = status ?? (disbursedUtc is null ? LoanStatus.PendingApproval : LoanStatus.Disbursed),
            Principal = principal,
            InitiationFee = initiation,
            DisbursedUtc = disbursedUtc,
            DecidedUtc = decidedUtc ?? disbursedUtc,
            MandateId = mandate?.Id
        };
        db.Loans.Add(loan);

        var n = 1;
        foreach (var due in dueDates ?? Array.Empty<DateTime>())
            db.LoanScheduleEntries.Add(new LoanScheduleEntry { LoanId = loan.Id, InstalmentNumber = n++, DueDate = due, InstalmentTotal = instalment });

        return loan;
    }

    private static PayNowTransaction PayNow(Tenant t, decimal amount, PaymentStatus status, DateTime completedUtc) => new()
    {
        TenantId = t.Id,
        PaymentReference = $"PN-{Guid.NewGuid():N}",
        Amount = amount,
        Status = status,
        CompletedUtc = completedUtc
    };
}
