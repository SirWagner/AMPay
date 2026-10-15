using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AMPay.Tests;

/// <summary>Money in: the ledger, arrears, and what the dashboard makes of them.</summary>
public class LoanLedgerTests
{
    // Tuesday 10 November 2026, 10:00 SAST.
    private static readonly DateTime Now = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 11, 10);

    // ----------------------------------------------------------------- the brief's two

    [Fact]
    public async Task AFailedDebit_LaterRecoveredByPayNow_NoLongerCountsAsFailed()
    {
        var (db, t) = await ArrangeAsync();
        var mandate = Mandate(db, t, "ACC1");
        var loan = AddLoan(db, t, mandate, new DateTime(2026, 11, 3), new DateTime(2026, 12, 3));
        await db.SaveChangesAsync();
        var ledger = Ledger(db);

        await ledger.ImportDebitResultsAsync(t.Id, new[] { new DebitResult("ACC1", new DateTime(2026, 11, 3), 1_000m, false, "02") });

        var before = await Dashboard(db).BuildAsync(t.Id, Now);
        Assert.Equal(1_000m, before.Collections.Failed);
        Assert.Equal(1, before.Collections.FailedDebits);
        Assert.Equal(1, before.Actions.Single(a => a.Key == "failed").Count);

        // The client pays the missed instalment through Pay Now.
        var payNow = new PayNowTransaction
        {
            TenantId = t.Id, ClientId = loan.ClientId, PaymentReference = "PN-1", Amount = 1_000m,
            Status = PaymentStatus.Complete, CompletedUtc = Now.AddHours(-1)
        };
        db.PayNowTransactions.Add(payNow);
        await db.SaveChangesAsync();
        Assert.True(await ledger.TryApplyPayNowAsync(payNow));

        var after = await Dashboard(db).BuildAsync(t.Id, Now);
        Assert.Equal(0m, after.Collections.Failed);
        Assert.Equal(0, after.Collections.FailedDebits);
        Assert.Equal(0, after.Actions.Single(a => a.Key == "failed").Count);
        Assert.Equal(1_000m, after.Collections.CollectedPayNow);

        // Still reported as a failure this month - just not a missing one.
        var reason = Assert.Single(after.FailureReasons);
        Assert.Equal("Insufficient funds", reason.Label);
        Assert.Equal(1, reason.Count);
        Assert.Equal(0, reason.Unrecovered);

        var failed = await db.LoanCollections.SingleAsync(c => c.Status == CollectionStatus.Failed);
        var paid = await db.LoanCollections.SingleAsync(c => c.Channel == CollectionChannel.PayNow);
        Assert.Equal(paid.Id, failed.RecoveredByCollectionId);
    }

    [Fact]
    public async Task Arrears_AreBucketedByEachLoansOldestUnpaidInstalment()
    {
        var (db, t) = await ArrangeAsync();
        // Oldest unpaid 45 days ago (and a newer one 15 days ago): 31-60.
        var a = AddLoan(db, t, null, Today.AddDays(-45), Today.AddDays(-15), Today.AddDays(15));
        // Oldest unpaid 95 days ago: 90+.
        AddLoan(db, t, null, Today.AddDays(-95), Today.AddDays(20));
        // First instalment paid, second 10 days late: 1-30.
        var c = AddLoan(db, t, null, Today.AddDays(-40), Today.AddDays(-10));
        // Up to date.
        AddLoan(db, t, null, Today.AddDays(5));
        // Settled and written-off loans are not in the book at all.
        AddLoan(db, t, null, LoanStatus.Settled, Today.AddDays(-200));
        AddLoan(db, t, null, LoanStatus.WrittenOff, Today.AddDays(-200));
        await db.SaveChangesAsync();

        await Ledger(db).RecordPaymentAsync(c, CollectionChannel.Manual, 1_000m, Now.AddDays(-39), "EFT", null, null);

        var p = await PortfolioMetrics.PositionAsync(db, t.Id, Today);

        Assert.Equal(new[] { 1, 1, 0, 1 }, p.Arrears.Select(b => b.Loans));
        Assert.Equal(1_000m, p.Arrears[0].Amount);  // loan c: one instalment left
        Assert.Equal(3_000m, p.Arrears[1].Amount);  // loan a: its whole balance, not just the overdue part
        Assert.Equal(2_000m, p.Arrears[3].Amount);
        Assert.Equal(4, p.ActiveLoans);
        Assert.Equal(7_000m, p.BookOutstanding);
        Assert.Equal(6_000m, p.BookInArrears);
        Assert.Equal(3, p.LoansInArrears);
        Assert.Equal(3, a.Schedule.Count);
    }

    // ----------------------------------------------------------------- the ledger

    [Fact]
    public async Task Payments_ClearTheOldestInstalmentFirst_AndAFullyPaidLoanSettles()
    {
        var (db, t) = await ArrangeAsync();
        var loan = AddLoan(db, t, null, Today.AddDays(-30), Today, Today.AddDays(30));
        await db.SaveChangesAsync();
        var ledger = Ledger(db);

        Assert.Null(await ledger.RecordPaymentAsync(loan, CollectionChannel.Manual, 1_500m, Now, "R1", null, null));
        var s = loan.Schedule.OrderBy(x => x.InstalmentNumber).ToList();
        Assert.Equal(new[] { 1_000m, 500m, 0m }, s.Select(x => x.PaidAmount));
        Assert.NotNull(s[0].PaidUtc);
        Assert.Null(s[1].PaidUtc);
        Assert.Equal(LoanStatus.Disbursed, loan.Status);

        // More than is owed is refused, not banked as credit.
        Assert.NotNull(await ledger.RecordPaymentAsync(loan, CollectionChannel.Manual, 1_500.01m, Now, "R2", null, null));

        Assert.Null(await ledger.RecordPaymentAsync(loan, CollectionChannel.Manual, 1_500m, Now, "R3", null, null));
        Assert.Equal(LoanStatus.Settled, loan.Status);
    }

    [Fact]
    public async Task ADebitReportedPaid_ThenReturnedUnpaid_IsReplacedNotDuplicated()
    {
        var (db, t) = await ArrangeAsync();
        var m = Mandate(db, t, "ACC9");
        var loan = AddLoan(db, t, m, new DateTime(2026, 11, 3));
        await db.SaveChangesAsync();
        var ledger = Ledger(db);

        var first = await ledger.ImportDebitResultsAsync(t.Id, new[] { new DebitResult("ACC9", new DateTime(2026, 11, 3), 1_000m, true) });
        Assert.Equal(1, first.Recorded);
        Assert.Equal(LoanStatus.Settled, loan.Status); // the only instalment was paid

        // Settled loans are no longer collected, so a late unpaid does not match. Put it back to test the replace.
        loan.Status = LoanStatus.Disbursed;
        await db.SaveChangesAsync();

        var second = await ledger.ImportDebitResultsAsync(t.Id, new[]
        {
            new DebitResult("ACC9", new DateTime(2026, 11, 3), 1_000m, false, "34"),
            new DebitResult("NOPE", new DateTime(2026, 11, 3), 500m, false, "02")
        });

        Assert.Equal(0, second.Recorded);
        Assert.Equal(1, second.Updated);
        Assert.Single(second.Unmatched);
        var row = await db.LoanCollections.SingleAsync();
        Assert.Equal(CollectionStatus.Failed, row.Status);
        Assert.Equal(CollectionFailureGroup.MandateCancelled, row.FailureGroup);
        Assert.Equal(0m, loan.Schedule.Single().PaidAmount);
    }

    [Fact]
    public async Task ADebitStillWithTheBank_IsNotArrears()
    {
        var (db, t) = await ArrangeAsync();
        var loan = AddLoan(db, t, null, Today.AddDays(-2));
        await db.SaveChangesAsync();
        db.LoanCollections.Add(new LoanCollection
        {
            TenantId = t.Id, LoanId = loan.Id, ScheduleEntryId = loan.Schedule.Single().Id,
            Channel = CollectionChannel.DebiCheck, ActionDate = Today.AddDays(-2), Amount = 1_000m, Status = CollectionStatus.Scheduled
        });
        await db.SaveChangesAsync();

        var p = await PortfolioMetrics.PositionAsync(db, t.Id, Today);
        Assert.Equal(0, p.LoansInArrears);
        Assert.Equal(1_000m, p.BookOutstanding);
    }

    [Theory]
    [InlineData("02", CollectionFailureGroup.InsufficientFunds)]
    [InlineData("2", CollectionFailureGroup.InsufficientFunds)]
    [InlineData("04", CollectionFailureGroup.Disputed)]
    [InlineData("34", CollectionFailureGroup.MandateCancelled)]
    [InlineData("99", CollectionFailureGroup.Other)]
    [InlineData(null, CollectionFailureGroup.Other)]
    public void UnpaidCodes_AreGrouped_UnknownOnesAsOther(string? code, CollectionFailureGroup expected) =>
        Assert.Equal(expected, UnpaidCodes.GroupOf(code));

    [Fact]
    public async Task WriteOff_RecordsWhatWasOwed_AndShowsInThisYearsTotal()
    {
        var (db, t) = await ArrangeAsync();
        var loan = AddLoan(db, t, null, Today.AddDays(-60), Today.AddDays(-30), Today);
        await db.SaveChangesAsync();
        var ledger = Ledger(db);
        await ledger.RecordPaymentAsync(loan, CollectionChannel.Manual, 1_000m, Now.AddDays(-59), null, null, null);

        Assert.NotNull(await ledger.WriteOffAsync(loan, "  ", null)); // a reason is required
        Assert.Null(await ledger.WriteOffAsync(loan, "Client emigrated; no contact.", null));

        Assert.Equal(LoanStatus.WrittenOff, loan.Status);
        Assert.Equal(2_000m, loan.WrittenOffAmount);
        Assert.Single(db.ClientNotes.Where(n => n.ClientId == loan.ClientId));

        var d = await Dashboard(db).BuildAsync(t.Id, DateTime.UtcNow);
        Assert.Equal(1, d.WrittenOffThisYear.Loans);
        Assert.Equal(2_000m, d.WrittenOffThisYear.Amount);
        Assert.Equal(0, d.Kpis.ActiveLoans);
    }

    // ----------------------------------------------------------------- dashboard figures

    [Fact]
    public async Task CapitalAvailable_IsCapitalLessPrincipalStillOut_AndNullUntilSet()
    {
        var (db, t) = await ArrangeAsync();
        // R 3,000 capitalised (all capital in this simple schedule) for R 2,400 paid out.
        AddLoan(db, t, null, Today.AddDays(5), Today.AddDays(35), Today.AddDays(65));
        await db.SaveChangesAsync();

        var unset = await Dashboard(db).BuildAsync(t.Id, Now);
        Assert.Null(unset.Collections.CapitalAvailable);
        Assert.Equal(2_400m, unset.Collections.OutstandingPrincipal);

        t.LendingCapital = 10_000m;
        await db.SaveChangesAsync();
        var set = await Dashboard(db).BuildAsync(t.Id, Now);
        Assert.Equal(7_600m, set.Collections.CapitalAvailable);
    }

    [Fact]
    public async Task Collections_FromAnotherLender_AreNeverCounted()
    {
        var (db, a) = await ArrangeAsync();
        var b = new Tenant { Name = "Other Lender" };
        db.Tenants.Add(b);
        var theirs = AddLoan(db, b, null, Today.AddDays(-5), Today.AddDays(25));
        await db.SaveChangesAsync();
        await Ledger(db).RecordPaymentAsync(theirs, CollectionChannel.Manual, 1_000m, Now, null, null, null);

        var d = await Dashboard(db).BuildAsync(a.Id, Now);
        Assert.Equal(0m, d.Collections.Collected);
        Assert.Equal(0m, d.Kpis.BookOutstanding);
        Assert.All(d.LentByMonth, m => Assert.Equal(0m, m.Recovered));
    }

    [Fact]
    public async Task TheDashboardWritesTodaysSnapshot_AndComparesWithTheStartOfTheMonth()
    {
        var (db, t) = await ArrangeAsync();
        AddLoan(db, t, null, Today.AddDays(5));
        db.PortfolioSnapshots.Add(new PortfolioSnapshot { TenantId = t.Id, SnapshotDate = new DateTime(2026, 11, 1), BookOutstanding = 800m, Arrears1To30 = 200m });
        await db.SaveChangesAsync();

        var d = await Dashboard(db).BuildAsync(t.Id, Now);

        Assert.Equal(new DateTime(2026, 11, 1), d.Kpis.ComparedToDate);
        Assert.Equal(800m, d.Kpis.BookOutstandingThen);
        Assert.Equal(0.25m, d.Kpis.ParRatioThen);
        Assert.True(await db.PortfolioSnapshots.AnyAsync(s => s.TenantId == t.Id && s.SnapshotDate == Today));

        // Opening it again the same day writes nothing more.
        await Dashboard(db).BuildAsync(t.Id, Now);
        Assert.Equal(2, await db.PortfolioSnapshots.CountAsync());
    }

    [Fact]
    public async Task TheFunnel_ComesFromThePortal_AndIsNullWhenItDoesNotAnswer()
    {
        var (db, t) = await ArrangeAsync();
        t.SelfServiceCode = "ABC234";
        await db.SaveChangesAsync();

        var ok = await new LenderDashboardService(db, new MemoryCache(new MemoryCacheOptions()), new FakePortal(new PortalFunnel(40, 12, 7)))
            .BuildAsync(t.Id, Now);
        Assert.Equal(new PortalFunnel(40, 12, 7), ok.Growth.Funnel);

        var slow = await new LenderDashboardService(db, new MemoryCache(new MemoryCacheOptions()), new FakePortal(null, hang: true))
            .BuildAsync(t.Id, Now);
        Assert.Null(slow.Growth.Funnel);
    }

    // ----------------------------------------------------------------- helpers

    private static async Task<(AppDbContext, Tenant)> ArrangeAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"ledger-{Guid.NewGuid()}").Options);
        var t = new Tenant { Name = "Albatross Money (Pty) Ltd", TradingName = "Albatross Money" };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return (db, t);
    }

    private static LoanLedgerService Ledger(AppDbContext db) => new(db, NullLogger<LoanLedgerService>.Instance);
    private static LenderDashboardService Dashboard(AppDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));

    private static DebiCheckMandate Mandate(AppDbContext db, Tenant t, string accountReference)
    {
        var m = new DebiCheckMandate { TenantId = t.Id, ClientId = Guid.NewGuid(), AccountReference = accountReference, Status = MandateStatus.Authenticated };
        db.Mandates.Add(m);
        return m;
    }

    private static int _loanNo;

    private static Loan AddLoan(AppDbContext db, Tenant t, DebiCheckMandate? mandate, params DateTime[] dueDates) =>
        AddLoan(db, t, mandate, LoanStatus.Disbursed, dueDates);

    /// <summary>R 1,000 instalments, all capital; R 800 of each is cash paid out (the rest a capitalised fee).</summary>
    private static Loan AddLoan(AppDbContext db, Tenant t, DebiCheckMandate? mandate, LoanStatus status, params DateTime[] dueDates)
    {
        var total = 1_000m * dueDates.Length;
        var loan = new Loan
        {
            TenantId = t.Id,
            ClientId = mandate?.ClientId ?? Guid.NewGuid(),
            CreditPackageId = Guid.NewGuid(),
            LoanNumber = $"LN-{Interlocked.Increment(ref _loanNo):D6}",
            Status = status,
            Principal = total * 0.8m,
            CapitalisedAmount = total,
            TotalRepayable = total,
            DisbursedUtc = Now.AddDays(-120),
            DecidedUtc = Now.AddDays(-120),
            MandateId = mandate?.Id
        };
        db.Loans.Add(loan);

        var n = 1;
        foreach (var due in dueDates)
            db.LoanScheduleEntries.Add(new LoanScheduleEntry
            {
                LoanId = loan.Id, InstalmentNumber = n++, DueDate = due, InstalmentTotal = 1_000m, CapitalPortion = 1_000m
            });

        return loan;
    }

    private sealed class FakePortal : IPortalApi
    {
        private readonly PortalFunnel? _funnel;
        private readonly bool _hang;
        public FakePortal(PortalFunnel? funnel, bool hang = false) { _funnel = funnel; _hang = hang; }

        public bool IsConfigured => true;
        public string? LinkFor(string? code) => null;

        public async Task<PortalFunnel?> FunnelAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            if (_hang) await Task.Delay(Timeout.Infinite, ct);
            return _funnel;
        }

        public Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
