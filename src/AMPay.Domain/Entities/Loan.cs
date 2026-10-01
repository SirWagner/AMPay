using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A credit agreement. Raised against an already-onboarded, activated client whose
/// supporting documents have been reviewed - never during capture.
/// <para>
/// This replaces the old one-to-one <c>ClientPayback</c> tab. A client borrows more than
/// once, so the relationship is one-to-many and each loan carries its own priced schedule,
/// its own affordability assessment and its own DebiCheck mandate.
/// </para>
/// <para>
/// Every monetary field below is an output of <c>ILoanPricingService</c>. Nothing here is
/// typed in by a capturer - the operator supplies the principal, the term and the package,
/// and the engine produces the rest.
/// </para>
/// </summary>
public class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public Guid CreditPackageId { get; set; }
    public CreditPackage? CreditPackage { get; set; }

    /// <summary>Per-tenant human reference, e.g. "LN-000014".</summary>
    public string LoanNumber { get; set; } = string.Empty;

    public LoanStatus Status { get; set; } = LoanStatus.Draft;

    // ---- What the operator asked for ----

    /// <summary>The amount the client actually receives.</summary>
    public decimal Principal { get; set; }

    public int NumberOfInstalments { get; set; }

    // ---- The rates this loan was priced at ----
    // Copied from the package at quote time. A package repriced next year must not silently
    // restate an agreement the client has already signed.

    public decimal MonthlyInterestRate { get; set; }
    public decimal MonthlyServiceFee { get; set; }
    public decimal CreditLifeRate { get; set; }

    // ---- What the engine calculated ----

    /// <summary>Charged once, capitalised into the balance. Clamped to the NCA maximum.</summary>
    public decimal InitiationFee { get; set; }

    /// <summary>Principal + initiation fee. This is what interest is charged on.</summary>
    public decimal CapitalisedAmount { get; set; }

    /// <summary>Level capital-and-interest portion of each instalment.</summary>
    public decimal BaseInstalment { get; set; }

    /// <summary>
    /// First instalment - the largest, because credit life is charged on a declining
    /// balance. This is the amount the DebiCheck mandate is raised for.
    /// </summary>
    public decimal FirstInstalment { get; set; }

    public decimal FinalInstalment { get; set; }

    public decimal TotalInterest { get; set; }
    public decimal TotalServiceFees { get; set; }
    public decimal TotalCreditLife { get; set; }

    /// <summary>Everything the client repays across the full term.</summary>
    public decimal TotalRepayable { get; set; }

    /// <summary>Total cost of credit: everything repaid above the principal.</summary>
    public decimal TotalCostOfCredit { get; set; }

    // ---- Collection terms (moved off ClientPayback) ----

    public DebitFrequency Frequency { get; set; } = DebitFrequency.Monthly;

    /// <summary>Collection day: "01".."31" or "LDOM", per Netcash AddMandate.</summary>
    public string? CollectionDay { get; set; }

    /// <summary>Netcash collection frequency day code (field 250, AN7).</summary>
    public string? CollectionDayCode { get; set; }

    public DateTime? FirstCollectionDate { get; set; }

    /// <summary>DebiCheck tracking days (field 232). Netcash accepts 1-10.</summary>
    public int TrackingDays { get; set; } = 5;

    public DateTime? AgreementDate { get; set; }

    // ---- Decision trail ----

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    public DateTime? DecidedUtc { get; set; }
    public string? DecidedByUserId { get; set; }
    public string? DecisionNotes { get; set; }

    public DateTime? DisbursedUtc { get; set; }

    public Guid? MandateId { get; set; }
    public DebiCheckMandate? Mandate { get; set; }

    public ICollection<LoanScheduleEntry> Schedule { get; set; } = new List<LoanScheduleEntry>();
    public ICollection<AffordabilityAssessment> Assessments { get; set; } = new List<AffordabilityAssessment>();

    /// <summary>The assessment the credit decision was actually taken on.</summary>
    public AffordabilityAssessment? LatestAssessment =>
        Assessments.OrderByDescending(a => a.AssessedUtc).FirstOrDefault();

    public bool IsEditable => Status is LoanStatus.Draft or LoanStatus.PendingApproval;
}

/// <summary>
/// One row of the amortisation schedule. Persisted rather than recomputed so that the
/// numbers the client signed for remain the numbers on file, whatever the engine does later.
/// </summary>
public class LoanScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LoanId { get; set; }
    public Loan? Loan { get; set; }

    /// <summary>1-based instalment number.</summary>
    public int InstalmentNumber { get; set; }

    public DateTime DueDate { get; set; }

    public decimal OpeningBalance { get; set; }

    public decimal InterestPortion { get; set; }
    public decimal CapitalPortion { get; set; }
    public decimal ServiceFee { get; set; }
    public decimal CreditLifePremium { get; set; }

    /// <summary>What the client is debited this month.</summary>
    public decimal InstalmentTotal { get; set; }

    public decimal ClosingBalance { get; set; }
}
