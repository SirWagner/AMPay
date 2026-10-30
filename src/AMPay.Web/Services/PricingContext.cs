using AMPay.Domain.Credit;
using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Everything about the lender and the client that a price depends on besides the package: the
/// caps in force today, whether the lender charges VAT, and how many short-term loans the client
/// has already had with this lender this calendar year (5% first, 3% after).
/// </summary>
public class PricingContext
{
    private readonly AppDbContext _db;
    private readonly RegulatoryCapsProvider _caps;

    public PricingContext(AppDbContext db, RegulatoryCapsProvider caps)
    {
        _db = db;
        _caps = caps;
    }

    public async Task<LoanQuoteRequest> RequestAsync(
        Guid tenantId, Guid clientId, decimal principal, int instalments, DateTime firstCollection,
        Guid? excludingLoanId = null, FeeOverride? feeOverride = null, CancellationToken ct = default)
    {
        var today = Sast.DateOf(DateTime.UtcNow);
        var caps = await _caps.ForDateAsync(today, ct);

        var vatNumber = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.VatNumber).FirstOrDefaultAsync(ct);

        var yearStartUtc = Sast.StartUtc(new DateTime(today.Year, 1, 1));
        var prior = await _db.Loans.AsNoTracking().CountAsync(l =>
            l.TenantId == tenantId && l.ClientId == clientId && l.Id != excludingLoanId &&
            (l.Status == LoanStatus.Approved || l.Status == LoanStatus.Disbursed ||
             l.Status == LoanStatus.Settled || l.Status == LoanStatus.WrittenOff) &&
            (l.DisbursedUtc ?? l.DecidedUtc) >= yearStartUtc &&
            l.Principal <= caps.ShortTermMaxPrincipal && l.NumberOfInstalments <= caps.ShortTermMaxMonths, ct);

        return new LoanQuoteRequest(principal, instalments, firstCollection)
        {
            Caps = caps,
            VatRegistered = !string.IsNullOrWhiteSpace(vatNumber),
            PriorShortTermLoansThisYear = prior,
            Override = feeOverride
        };
    }
}
