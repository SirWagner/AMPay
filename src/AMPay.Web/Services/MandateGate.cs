using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using AMPay.Web.Controllers;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// A debit mandate, DebiCheck or eMandate, may only be raised for a loan whose current quote the
/// client has accepted and whose current agreement they have signed. One rule for every screen.
/// </summary>
public record MandateGate(Guid LoanId, string LoanNumber, bool QuoteAccepted, bool AgreementSigned)
{
    public bool Ready => QuoteAccepted && AgreementSigned;

    public string? Problem => Ready ? null
        : $"The debit mandate for {LoanNumber} is waiting for the client: " +
          string.Join(" and ", new[]
          {
              QuoteAccepted ? null : "the quote must be accepted",
              AgreementSigned ? null : "the agreement must be signed"
          }.Where(s => s is not null)) + " first.";

    /// <summary>The latest quote that still stands decides: an older acceptance does not cover new terms.</summary>
    public static bool IsAccepted(IEnumerable<CreditQuote> quotes) =>
        quotes.Where(q => q.Status is not (QuoteStatus.Voided or QuoteStatus.Expired))
            .MaxBy(q => q.Issue)?.Status == QuoteStatus.Accepted;

    public static bool IsSigned(IEnumerable<LoanContract> contracts) =>
        contracts.Where(c => c.Status != ContractStatus.Voided)
            .MaxBy(c => c.Issue)?.Status == ContractStatus.Signed;

    public static async Task<MandateGate> ForAsync(AppDbContext db, Loan loan, CancellationToken ct = default) =>
        new(loan.Id, loan.LoanNumber,
            IsAccepted(await db.CreditQuotes.AsNoTracking().Where(q => q.LoanId == loan.Id).ToListAsync(ct)),
            IsSigned(await db.LoanContracts.AsNoTracking().Where(c => c.LoanId == loan.Id).ToListAsync(ct)));

    /// <summary>The client's approved or paid-out loans that have no working mandate yet, newest first.</summary>
    public static async Task<List<MandateGate>> WaitingAsync(AppDbContext db, Guid clientId, CancellationToken ct = default)
    {
        var loans = await db.Loans.AsNoTracking().Include(l => l.Mandate)
            .Where(l => l.ClientId == clientId && (l.Status == LoanStatus.Approved || l.Status == LoanStatus.Disbursed))
            .OrderByDescending(l => l.CreatedUtc).ToListAsync(ct);

        var gates = new List<MandateGate>();
        foreach (var l in loans.Where(l => l.Mandate is null || MandatesController.IsDeadMandate(l.Mandate.Status)))
            gates.Add(await ForAsync(db, l, ct));
        return gates;
    }

    /// <summary>An eMandate the client has not answered: it may be sent again.</summary>
    public static bool IsUnsignedEMandate(DebiCheckMandate? m) =>
        m is { MandateType: MandateType.NetcashEMandate, Status: MandateStatus.AwaitingDebtorAuthentication };

    /// <summary>Why a mandate cannot be raised for this loan now, or null when it can.</summary>
    public static async Task<string?> ProblemForAsync(AppDbContext db, Loan loan, CancellationToken ct = default, bool resendEMandate = false)
    {
        if (loan.Status is not (LoanStatus.Approved or LoanStatus.Disbursed))
            return "A debit mandate can only be originated for an approved loan.";

        // A mandate that failed, was rejected or expired collects nothing, so it does not block another try.
        if (loan.MandateId is not null)
        {
            var existing = await db.Mandates.AsNoTracking().FirstOrDefaultAsync(m => m.Id == loan.MandateId, ct);
            if (existing is not null && !MandatesController.IsDeadMandate(existing.Status) &&
                !(resendEMandate && IsUnsignedEMandate(existing)))
                return "This loan already has a debit mandate.";
        }

        return (await ForAsync(db, loan, ct)).Problem;
    }
}
