using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A tenant's credit package - the price list a loan is quoted against.
/// <para>
/// Every rate lives here rather than in code because the NCA caps move (they are gazetted
/// and reviewed), and because each lender trading under the AM-Pay ISV prices differently.
/// A hard-coded 5% would be a compliance incident the day the cap changes.
/// </para>
/// <para>
/// Rates are stored as decimal fractions, not percentages: 5% per month is 0.05.
/// </para>
/// </summary>
public class CreditPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public CreditTier Tier { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // ---- Pricing ----

    /// <summary>
    /// Interest per month on the reducing balance. NCA short-term credit caps this at 5%
    /// per month for a first advance and 3% per month thereafter.
    /// </summary>
    public decimal MonthlyInterestRate { get; set; }

    /// <summary>
    /// Monthly service fee, VAT inclusive. The NCA ceiling is well above what we charge;
    /// a cap is a maximum, not a target.
    /// </summary>
    public decimal MonthlyServiceFee { get; set; }

    /// <summary>
    /// Initiation fee as a fraction of the principal. Uniform across tiers today, but held
    /// per package so a tier can diverge without a schema change. Always clamped to the
    /// statutory maximum at quote time - see <c>NcaCreditLimits</c>.
    /// </summary>
    public decimal InitiationFeeRate { get; set; }

    /// <summary>
    /// Credit life insurance per month as a fraction of the outstanding deferred amount.
    /// The statutory cap is R4.50 per R1 000, i.e. 0.0045.
    /// </summary>
    public decimal CreditLifeRate { get; set; }

    // ---- Limits ----

    public decimal MinLoanAmount { get; set; }
    public decimal MaxLoanAmount { get; set; }
    public int MinTermMonths { get; set; }
    public int MaxTermMonths { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    public ICollection<Loan> Loans { get; set; } = new List<Loan>();
}
