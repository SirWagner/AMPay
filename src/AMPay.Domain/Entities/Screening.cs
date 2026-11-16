namespace AMPay.Domain.Entities;

/// <summary>
/// An employer the lender's clients work for. Captured once and picked from a list on the
/// Employment step, so a second client at the same company does not mean typing it all again.
/// Each lender keeps its own list.
/// </summary>
public class Employer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The branch or site that pays, where an employer pays from more than one.</summary>
    public string? PayPoint { get; set; }

    /// <summary>The HR or payroll number staff phone to confirm someone works there.</summary>
    public string? Telephone { get; set; }
    public string? ContactPerson { get; set; }
    public string? EmailAddress { get; set; }
    public string? Address { get; set; }

    /// <summary>Monthly, Fortnightly or Weekly: copied onto a client when the employer is chosen.</summary>
    public string? PayFrequency { get; set; }
    public int? SalaryDay { get; set; }

    /// <summary>Someone at the lender has confirmed the company exists and the number above is real.</summary>
    public bool IsVerified { get; set; }

    /// <summary>Hidden from the picker but kept, because past clients still point at it.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(PayPoint) ? Name : $"{Name} ({PayPoint})";
}

/// <summary>
/// One "good to go" check on a client: the lender's own pre-screen, run on what is already on
/// file and costing nothing, before the paid credit bureau check is allowed. A new row is written
/// whenever the outcome or score changes, so the history shows what changed and when.
/// </summary>
public class ClientScreening
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public ScreeningOutcome Outcome { get; set; }

    /// <summary>0 to 100. Indicative only: the outcome is what gates the credit check.</summary>
    public int Score { get; set; }

    /// <summary>No loan with this lender before.</summary>
    public bool IsFirstTimeApplicant { get; set; }

    /// <summary>One line per check: "Fail|Employment|Unemployed". See <see cref="Credit.GoodToGo"/>.</summary>
    public string Checks { get; set; } = string.Empty;

    public ScreeningTrigger Trigger { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public string? CheckedBy { get; set; }

    /// <summary>
    /// A "Review" outcome let through to the credit check by an administrator, and why. Cleared
    /// by the next change, because the override was given for the facts as they were.
    /// </summary>
    public string? OverrideReason { get; set; }
    public string? OverriddenBy { get; set; }
    public DateTime? OverriddenUtc { get; set; }

    public bool AllowsCreditCheck =>
        Outcome == ScreeningOutcome.GoodToGo ||
        (Outcome == ScreeningOutcome.Review && OverrideReason is not null);
}

public enum ScreeningOutcome
{
    /// <summary>Everything checked out. The credit bureau check may be run.</summary>
    GoodToGo = 0,
    /// <summary>Something is missing or needs a person to look at it.</summary>
    Review = 1,
    /// <summary>A rule the lender will not lend past: unemployed, under 18, written off before.</summary>
    NotGoodToGo = 2
}

public enum ScreeningTrigger
{
    /// <summary>Someone pressed "Recheck now".</summary>
    Manual = 0,
    /// <summary>The client's details changed and the next page that showed the flag re-ran it.</summary>
    DetailsChanged = 1,
    /// <summary>The daily recheck found the last result older than the recheck period.</summary>
    Scheduled = 2
}

/// <summary>
/// A verification of one fact about a client: that they are alive (Home Affairs), or that they
/// work where they say. Kept as history; the latest of each kind is what counts.
/// </summary>
public class ClientVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public VerificationKind Kind { get; set; }
    public VerificationResult Result { get; set; }

    /// <summary>How it was checked: "Home Affairs (simulated)", "Phoned the employer", "Payslip".</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Who confirmed it at the other end, or the provider's reference.</summary>
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>True when the answer came from a stub, not a real provider.</summary>
    public bool Simulated { get; set; }

    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public string? CheckedBy { get; set; }
}

public enum VerificationKind
{
    /// <summary>The ID number belongs to a living person (Home Affairs population register).</summary>
    Life = 0,
    Employment = 1
}

public enum VerificationResult
{
    Confirmed = 0,
    /// <summary>Home Affairs records the person as deceased, or the employer does not know them.</summary>
    Failed = 1,
    /// <summary>Asked, but no answer could be had (provider down, nobody picked up).</summary>
    Inconclusive = 2
}

/// <summary>
/// An assessment of a client's bank statement: how they handle money, read from the statement
/// by an AI model (or the stub), with a Good / Warning / Bad flag. Advice for the lender, not a
/// decision.
/// </summary>
public class BankStatementAssessment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public Guid DocumentId { get; set; }
    public ClientDocument? Document { get; set; }

    public StatementFlag Flag { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>The individual observations, one per line.</summary>
    public string? Observations { get; set; }

    public decimal? AverageMonthlyIncome { get; set; }
    public decimal? AverageMonthlySpend { get; set; }

    /// <summary>The model used, or "stub".</summary>
    public string Model { get; set; } = string.Empty;
    public bool Simulated { get; set; }

    /// <summary>When the client's consent to sending the statement for analysis was recorded (POPIA s72).</summary>
    public DateTime ConsentRecordedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A bank statement checked with the rules model (<see cref="Credit.StatementModel"/>): the
/// figures a staff member read off the statement, what the client had declared at the time, and
/// the verdict. Kept as history; the latest one feeds the good-to-go check.
/// </summary>
public class StatementReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>The uploaded statement the figures were read from, when there is one on file.</summary>
    public Guid? DocumentId { get; set; }

    public int MonthsCovered { get; set; }
    public int SalaryDeposits { get; set; }
    public decimal AverageSalaryDeposit { get; set; }
    public int ReturnedDebitOrders { get; set; }
    public int OtherLenders { get; set; }
    public decimal MonthlyLoanRepayments { get; set; }
    public decimal MonthlyGambling { get; set; }
    public int DaysOverdrawn { get; set; }

    /// <summary>What the client had declared when the statement was checked, so the comparison can be read back later.</summary>
    public decimal? DeclaredNetIncome { get; set; }
    public decimal DeclaredDebtInstalments { get; set; }

    public StatementFlag Flag { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>One line per finding, stored as the good-to-go checks are.</summary>
    public string Findings { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

public enum StatementFlag
{
    Good = 0,
    Warning = 1,
    Bad = 2
}
