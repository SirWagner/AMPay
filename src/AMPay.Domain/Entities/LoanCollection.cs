using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// One attempt to collect money for a loan, or one payment received for it: a DebiCheck debit
/// and its result, a Pay Now checkout, or a payment recorded by hand. This is the loan's ledger
/// of money in. Instalments are marked paid from it (see <c>LoanLedger</c>), never typed in.
/// </summary>
public class LoanCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid LoanId { get; set; }
    public Loan? Loan { get; set; }

    /// <summary>The instalment a debit was raised for. Null for a payment not tied to one instalment.</summary>
    public Guid? ScheduleEntryId { get; set; }
    public LoanScheduleEntry? ScheduleEntry { get; set; }

    public CollectionChannel Channel { get; set; }

    /// <summary>The date the debit ran or the payment was due/made (a South African calendar date).</summary>
    public DateTime ActionDate { get; set; }

    public decimal Amount { get; set; }

    public CollectionStatus Status { get; set; } = CollectionStatus.Scheduled;

    /// <summary>The bank's unpaid code exactly as Netcash reported it, e.g. "02".</summary>
    public string? FailureCode { get; set; }

    public CollectionFailureGroup? FailureGroup { get; set; }

    /// <summary>
    /// For a failed debit: the later payment that cleared the instalment it was for. A failure
    /// with this set is no longer money missing.
    /// </summary>
    public Guid? RecoveredByCollectionId { get; set; }

    /// <summary>When the money arrived. Null unless <see cref="CollectionStatus.Successful"/>.</summary>
    public DateTime? ReceivedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    /// <summary>Netcash's reference for a debit, or the EFT/receipt reference for a manual payment.</summary>
    public string? ExternalReference { get; set; }

    public string? Notes { get; set; }

    public Guid? NetcashBatchId { get; set; }
    public NetcashBatch? NetcashBatch { get; set; }

    public Guid? PayNowTransactionId { get; set; }
    public PayNowTransaction? PayNowTransaction { get; set; }
}

/// <summary>
/// A lender's book as it stood at the end of one South African day. Written once a day so the
/// dashboard can compare this month with last; nothing else reads it.
/// </summary>
public class PortfolioSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>The SAST calendar date this describes.</summary>
    public DateTime SnapshotDate { get; set; }

    public int ActiveLoans { get; set; }

    /// <summary>Everything still to be repaid on loans being collected.</summary>
    public decimal BookOutstanding { get; set; }

    // Bucketed by each loan's oldest unpaid instalment; amounts are the loan's whole balance.
    // Book in arrears is the sum of the four.

    public int Arrears1To30Count { get; set; }
    public decimal Arrears1To30 { get; set; }
    public int Arrears31To60Count { get; set; }
    public decimal Arrears31To60 { get; set; }
    public int Arrears61To90Count { get; set; }
    public decimal Arrears61To90 { get; set; }
    public int Arrears90PlusCount { get; set; }
    public decimal Arrears90Plus { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
