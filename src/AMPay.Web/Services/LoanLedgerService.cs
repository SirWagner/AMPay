using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AMPay.Web.Services;

/// <summary>One line of a Netcash debit-order result: what happened to one debit.</summary>
/// <param name="AccountReference">The mandate's account reference (field 101) the debit ran against.</param>
/// <param name="ActionDate">The date the debit was presented.</param>
/// <param name="UnpaidCode">The bank's unpaid code when it failed, otherwise null.</param>
public record DebitResult(
    string AccountReference,
    DateTime ActionDate,
    decimal Amount,
    bool Successful,
    string? UnpaidCode = null,
    string? ExternalReference = null,
    Guid? NetcashBatchId = null);

public record DebitImportSummary(int Recorded, int Updated, IReadOnlyList<string> Unmatched);

/// <summary>
/// Every write to a loan's money-in ledger goes through here: payments recorded by hand, Pay Now
/// checkouts, and debit results from Netcash. After each one the loan's instalments are
/// re-marked from the full ledger (see <see cref="LoanLedger"/>), and a loan paid in full is
/// settled.
/// </summary>
public class LoanLedgerService
{
    private readonly AppDbContext _db;
    private readonly ILogger<LoanLedgerService> _log;
    private readonly IMemoryCache? _cache;

    public LoanLedgerService(AppDbContext db, ILogger<LoanLedgerService> log, IMemoryCache? cache = null)
    {
        _db = db;
        _log = log;
        _cache = cache;
    }

    /// <summary>Saves, then drops the lender's cached dashboard so the change shows at once.</summary>
    private async Task SaveAsync(Guid tenantId, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        _cache?.Remove(LenderDashboardService.CacheKey(tenantId));
    }

    /// <summary>
    /// Records money received for a loan. Returns an error for the person on screen, or null.
    /// The caller has already checked the user may act for the loan's tenant.
    /// </summary>
    public async Task<string?> RecordPaymentAsync(
        Loan loan, CollectionChannel channel, decimal amount, DateTime receivedUtc,
        string? reference, string? notes, string? userId, Guid? payNowTransactionId = null,
        CancellationToken ct = default)
    {
        if (loan.Status != LoanStatus.Disbursed)
            return "Payments can only be recorded against a loan that has been paid out and is being collected.";
        if (amount <= 0m)
            return "Enter the amount received.";

        await LoadLedgerAsync(loan, ct);

        var outstanding = LoanLedger.Outstanding(loan.Schedule);
        if (amount > outstanding)
            return $"That is more than the R {outstanding:N2} still owed on this loan.";

        _db.LoanCollections.Add(new LoanCollection
        {
            TenantId = loan.TenantId,
            LoanId = loan.Id,
            Channel = channel,
            ActionDate = Sast.DateOf(receivedUtc),
            Amount = amount,
            Status = CollectionStatus.Successful,
            ReceivedUtc = receivedUtc,
            ExternalReference = Trim(reference, 100),
            Notes = Trim(notes, 500),
            CreatedByUserId = userId,
            PayNowTransactionId = payNowTransactionId
        });

        // Allocate over the ledger including the new row: it is tracked, so it is in Collections.
        Reallocate(loan);
        await SaveAsync(loan.TenantId, ct);
        return null;
    }

    /// <summary>
    /// A completed Pay Now checkout pays a loan when the client has exactly one loan being
    /// collected. With none or several, it stays a tenant-level payment for someone to apply.
    /// </summary>
    public async Task<bool> TryApplyPayNowAsync(PayNowTransaction payment, CancellationToken ct = default)
    {
        if (payment.Status != PaymentStatus.Complete || payment.ClientId is null) return false;
        if (await _db.LoanCollections.AnyAsync(c => c.PayNowTransactionId == payment.Id, ct)) return false;

        var loans = await _db.Loans
            .Where(l => l.TenantId == payment.TenantId && l.ClientId == payment.ClientId && l.Status == LoanStatus.Disbursed)
            .Take(2)
            .ToListAsync(ct);
        if (loans.Count != 1) return false;

        var error = await RecordPaymentAsync(loans[0], CollectionChannel.PayNow, payment.Amount,
            payment.CompletedUtc ?? DateTime.UtcNow, payment.PaymentReference, null, null, payment.Id, ct);

        if (error is not null)
        {
            _log.LogWarning("Pay Now {Reference} not applied to loan {LoanNumber}: {Error}",
                payment.PaymentReference, loans[0].LoanNumber, error);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Records Netcash debit results for one tenant. A second result for the same debit (a
    /// success later returned unpaid) replaces the first. Lines that match no loan are
    /// returned, not guessed at.
    /// </summary>
    public async Task<DebitImportSummary> ImportDebitResultsAsync(
        Guid tenantId, IEnumerable<DebitResult> results, CancellationToken ct = default)
    {
        var lines = results.ToList();
        var refs = lines.Select(r => r.AccountReference).Distinct().ToList();

        // One query for every loan the file mentions.
        var loans = await _db.Loans
            .Include(l => l.Mandate)
            .Where(l => l.TenantId == tenantId && l.Status == LoanStatus.Disbursed &&
                        l.Mandate != null && refs.Contains(l.Mandate.AccountReference))
            .ToListAsync(ct);

        var recorded = 0;
        var updated = 0;
        var unmatched = new List<string>();
        var touched = new HashSet<Loan>();

        foreach (var line in lines)
        {
            var candidates = loans.Where(l => l.Mandate!.AccountReference == line.AccountReference).ToList();
            if (candidates.Count == 0)
            {
                unmatched.Add($"{line.AccountReference} {line.ActionDate:yyyy-MM-dd}: no loan being collected on this mandate");
                continue;
            }

            foreach (var c in candidates) await LoadLedgerAsync(c, ct);

            var date = line.ActionDate.Date;
            var (loan, entry) = candidates
                .Select(l => (Loan: l, Entry: l.Schedule.FirstOrDefault(s => s.DueDate.Date == date)))
                .FirstOrDefault(x => x.Entry is not null);
            if (loan is null)
            {
                // A debit on a date the schedule does not have (a re-presentation, say): book it to
                // the loan when there is only one, unallocated to any instalment.
                if (candidates.Count > 1)
                {
                    unmatched.Add($"{line.AccountReference} {date:yyyy-MM-dd}: several loans on this mandate and no instalment due that day");
                    continue;
                }
                loan = candidates[0];
            }

            var existing = loan.Collections.FirstOrDefault(c =>
                c.Channel == CollectionChannel.DebiCheck && c.ActionDate.Date == date &&
                (entry is null ? c.ScheduleEntryId == null : c.ScheduleEntryId == entry.Id));

            var row = existing ?? new LoanCollection
            {
                TenantId = tenantId,
                LoanId = loan.Id,
                ScheduleEntryId = entry?.Id,
                Channel = CollectionChannel.DebiCheck,
                ActionDate = date
            };

            row.Amount = line.Amount;
            row.ExternalReference = Trim(line.ExternalReference, 100) ?? row.ExternalReference;
            row.NetcashBatchId = line.NetcashBatchId ?? row.NetcashBatchId;

            if (line.Successful)
            {
                row.Status = CollectionStatus.Successful;
                row.ReceivedUtc ??= Sast.StartUtc(date);
                row.FailureCode = null;
                row.FailureGroup = null;
            }
            else
            {
                row.Status = CollectionStatus.Failed;
                row.ReceivedUtc = null;
                row.FailureCode = Trim(line.UnpaidCode, 10);
                row.FailureGroup = UnpaidCodes.GroupOf(line.UnpaidCode);
            }

            if (existing is null)
            {
                _db.LoanCollections.Add(row);
                recorded++;
            }
            else
            {
                updated++;
            }

            touched.Add(loan);
        }

        foreach (var loan in touched) Reallocate(loan);
        await SaveAsync(tenantId, ct);

        _log.LogInformation("Debit results for tenant {TenantId}: {Recorded} recorded, {Updated} updated, {Unmatched} unmatched.",
            tenantId, recorded, updated, unmatched.Count);

        return new DebitImportSummary(recorded, updated, unmatched);
    }

    /// <summary>Stops expecting the balance back. Only for a loan being collected.</summary>
    public async Task<string?> WriteOffAsync(Loan loan, string? reason, string? userId, CancellationToken ct = default)
    {
        if (loan.Status != LoanStatus.Disbursed)
            return "Only a loan being collected can be written off.";
        if (string.IsNullOrWhiteSpace(reason))
            return "Record why this loan is being written off.";

        await LoadLedgerAsync(loan, ct);

        loan.Status = LoanStatus.WrittenOff;
        loan.WrittenOffUtc = DateTime.UtcNow;
        loan.WrittenOffAmount = LoanLedger.Outstanding(loan.Schedule);

        _db.ClientNotes.Add(new ClientNote
        {
            ClientId = loan.ClientId,
            Category = "Write-off",
            Body = $"Loan {loan.LoanNumber} written off with R {loan.WrittenOffAmount:N2} outstanding. {reason.Trim()}",
            CreatedByUserId = userId
        });

        await SaveAsync(loan.TenantId, ct);
        return null;
    }

    // ---------------------------------------------------------------- helpers

    private async Task LoadLedgerAsync(Loan loan, CancellationToken ct)
    {
        var entry = _db.Entry(loan);
        if (!entry.Collection(l => l.Schedule).IsLoaded) await entry.Collection(l => l.Schedule).LoadAsync(ct);
        if (!entry.Collection(l => l.Collections).IsLoaded) await entry.Collection(l => l.Collections).LoadAsync(ct);
    }

    private void Reallocate(Loan loan)
    {
        // Rows added through the DbSet are fixed up into the tracked loan's collection.
        var allocation = LoanLedger.Allocate(loan.Schedule, loan.Collections);

        if (allocation.FullyPaid && loan.Status == LoanStatus.Disbursed)
        {
            loan.Status = LoanStatus.Settled;
            _log.LogInformation("Loan {LoanNumber} is paid in full and now settled.", loan.LoanNumber);
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}
