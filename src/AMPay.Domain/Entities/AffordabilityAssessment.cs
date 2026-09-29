using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A National Credit Act s78-81 affordability assessment, recorded against a loan.
/// <para>
/// The NCA does not merely require that the assessment be done - it requires that it be
/// evidenced. s81(2) makes an advance reckless if the lender failed to conduct the
/// assessment, and the lender carries the burden of proving it did. So the inputs are
/// snapshotted here rather than read back off the client record, which changes over time.
/// </para>
/// </summary>
public class AffordabilityAssessment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LoanId { get; set; }
    public Loan? Loan { get; set; }

    public DateTime AssessedUtc { get; set; } = DateTime.UtcNow;
    public string? AssessedByUserId { get; set; }

    // ---- Inputs, as they stood at assessment time ----

    public decimal GrossMonthlyIncome { get; set; }
    public decimal NetMonthlyIncome { get; set; }

    /// <summary>What the client declared they spend.</summary>
    public decimal DeclaredMonthlyExpenses { get; set; }

    /// <summary>Existing instalments to other credit providers.</summary>
    public decimal ExistingDebtRepayments { get; set; }

    /// <summary>
    /// The Regulation 23A(7) minimum expense norm for this income band. The assessment uses
    /// the greater of this and the declared figure - a client who under-declares their
    /// living costs cannot be lent into hardship on the strength of it.
    /// </summary>
    public decimal StatutoryMinimumExpenses { get; set; }

    /// <summary>The instalment being tested.</summary>
    public decimal ProposedInstalment { get; set; }

    // ---- Outputs ----

    /// <summary>Net income less the applied expense floor and existing debt.</summary>
    public decimal DiscretionaryIncome { get; set; }

    /// <summary>Discretionary income remaining after the proposed instalment.</summary>
    public decimal SurplusAfterInstalment { get; set; }

    /// <summary>Proposed instalment as a fraction of discretionary income.</summary>
    public decimal UtilisationRatio { get; set; }

    public AffordabilityOutcome Outcome { get; set; }

    /// <summary>Plain-language reasoning, shown to the operator and retained for audit.</summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Set when a human approved a loan the engine flagged Marginal or Fail. An override is
    /// not forbidden, but it must be deliberate, attributed and recorded.
    /// </summary>
    public bool WasOverridden { get; set; }
    public string? OverrideReason { get; set; }
    public string? OverriddenByUserId { get; set; }
}
