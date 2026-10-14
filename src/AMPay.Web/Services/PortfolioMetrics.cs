using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <param name="Loans">Loans whose oldest unpaid instalment falls in this bucket.</param>
/// <param name="Amount">Those loans' whole outstanding balance, not just the overdue part.</param>
public record ArrearsBucket(string Label, int Loans, decimal Amount);

/// <summary>A lender's book on one SAST day.</summary>
public record PortfolioPosition(
    int ActiveLoans,
    decimal BookOutstanding,
    decimal OutstandingPrincipal,
    IReadOnlyList<ArrearsBucket> Arrears)
{
    public decimal BookInArrears => Arrears.Sum(b => b.Amount);
    public int LoansInArrears => Arrears.Sum(b => b.Loans);

    /// <summary>Portfolio at risk: the share of the book on loans that are behind. Null with no book.</summary>
    public decimal? ParRatio => BookOutstanding == 0m ? null : BookInArrears / BookOutstanding;
}

/// <summary>
/// The book and its arrears, worked out from the schedule's paid amounts. Shared by the
/// dashboard (today) and the nightly snapshot (each day's close), so both always agree.
/// </summary>
public static class PortfolioMetrics
{
    public static async Task<PortfolioPosition> PositionAsync(AppDbContext db, Guid tenantId, DateTime todaySast, CancellationToken ct = default)
    {
        var today = todaySast.Date;

        var unpaid =
            from s in db.LoanScheduleEntries.AsNoTracking()
            join l in db.Loans.AsNoTracking() on s.LoanId equals l.Id
            where l.TenantId == tenantId && l.Status == LoanStatus.Disbursed && s.PaidAmount < s.InstalmentTotal
            select new { s, l };

        // One row per loan being collected: what is still owed, and how much of that is the
        // cash the lender paid out (capital scaled back from the capitalised amount, which
        // includes the initiation fee).
        var perLoan = await unpaid
            .GroupBy(x => x.l.Id)
            .Select(g => new
            {
                LoanId = g.Key,
                Outstanding = g.Sum(x => x.s.InstalmentTotal - x.s.PaidAmount),
                Principal = g.Sum(x => x.l.CapitalisedAmount == 0m
                    ? 0m
                    : x.s.CapitalPortion * (x.s.InstalmentTotal - x.s.PaidAmount) / x.s.InstalmentTotal * x.l.Principal / x.l.CapitalisedAmount)
            })
            .ToListAsync(ct);

        // Overdue instalments, leaving out any whose debit is still with the bank: it is not
        // late until the bank says it failed.
        var overdue = await unpaid
            .Where(x => x.s.DueDate < today &&
                        !db.LoanCollections.Any(c => c.ScheduleEntryId == x.s.Id && c.Status == CollectionStatus.Scheduled))
            .GroupBy(x => x.l.Id)
            .Select(g => new { LoanId = g.Key, Oldest = g.Min(x => x.s.DueDate) })
            .ToListAsync(ct);

        var activeLoans = await db.Loans.AsNoTracking()
            .CountAsync(l => l.TenantId == tenantId && l.Status == LoanStatus.Disbursed, ct);

        var outstandingByLoan = perLoan.ToDictionary(x => x.LoanId, x => x.Outstanding);
        var counts = new int[4];
        var amounts = new decimal[4];
        foreach (var o in overdue)
        {
            var bucket = ArrearsBuckets.For((today - o.Oldest.Date).Days);
            if (bucket < 0) continue;
            counts[bucket]++;
            amounts[bucket] += outstandingByLoan.GetValueOrDefault(o.LoanId);
        }

        return new PortfolioPosition(
            activeLoans,
            perLoan.Sum(x => x.Outstanding),
            Math.Round(perLoan.Sum(x => x.Principal), 2),
            Enumerable.Range(0, 4).Select(i => new ArrearsBucket(ArrearsBuckets.Labels[i], counts[i], amounts[i])).ToList());
    }

    public static PortfolioSnapshot ToSnapshot(Guid tenantId, DateTime sastDate, PortfolioPosition p) => new()
    {
        TenantId = tenantId,
        SnapshotDate = sastDate.Date,
        ActiveLoans = p.ActiveLoans,
        BookOutstanding = p.BookOutstanding,
        Arrears1To30Count = p.Arrears[0].Loans,
        Arrears1To30 = p.Arrears[0].Amount,
        Arrears31To60Count = p.Arrears[1].Loans,
        Arrears31To60 = p.Arrears[1].Amount,
        Arrears61To90Count = p.Arrears[2].Loans,
        Arrears61To90 = p.Arrears[2].Amount,
        Arrears90PlusCount = p.Arrears[3].Loans,
        Arrears90Plus = p.Arrears[3].Amount
    };

    /// <summary>Writes today's snapshot for every lender that lacks one. Safe to run any number of times.</summary>
    public static async Task<int> SnapshotAllAsync(AppDbContext db, DateTime utcNow, CancellationToken ct = default)
    {
        var today = Sast.DateOf(utcNow);

        var due = await db.Tenants.AsNoTracking()
            .Where(t => !t.IsPlatformOwner &&
                        !db.PortfolioSnapshots.Any(s => s.TenantId == t.Id && s.SnapshotDate == today) &&
                        db.Loans.Any(l => l.TenantId == t.Id && l.DisbursedUtc != null))
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in due)
        {
            var position = await PositionAsync(db, tenantId, today, ct);
            db.PortfolioSnapshots.Add(ToSnapshot(tenantId, today, position));
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}

/// <summary>
/// Writes each lender's snapshot once a day at 00:30 SAST, describing the day just closed.
/// <para>
/// Off unless <c>PortfolioSnapshots:Nightly</c> is true. On a pausing serverless database
/// (the free Azure SQL offer) a nightly wake-up costs about an hour of compute every day, so
/// there the dashboard writes the day's snapshot instead, the first time it is opened, when the
/// database is awake anyway. Turn this on once the database no longer pauses.
/// </para>
/// </summary>
public class PortfolioSnapshotService : BackgroundService
{
    public const string NightlySetting = "PortfolioSnapshots:Nightly";

    private static readonly TimeSpan RunAt = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PortfolioSnapshotService> _log;

    public PortfolioSnapshotService(IServiceScopeFactory scopes, ILogger<PortfolioSnapshotService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = Sast.StartUtc(Sast.DateOf(now)) + RunAt;
            if (next <= now) next = Sast.StartUtc(Sast.DateOf(now).AddDays(1)) + RunAt;

            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var written = await PortfolioMetrics.SnapshotAllAsync(db, DateTime.UtcNow, stoppingToken);
                _log.LogInformation("Portfolio snapshots written for {Count} lender(s).", written);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A paused database or a blip: the dashboard fills the gap when next opened.
                _log.LogWarning("Nightly portfolio snapshot skipped: {Message}", ex.GetBaseException().Message);
            }
        }
    }
}
