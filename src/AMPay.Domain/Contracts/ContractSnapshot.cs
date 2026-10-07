using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AMPay.Domain.Contracts;

/// <summary>
/// Everything a contract pack says, frozen at the moment it is issued.
/// <para>
/// Nothing in here is looked up again later. If the client's address changes, the package
/// is repriced or the template is reworded, a pack already issued still reads exactly as it
/// did - which is the point: it is the record of what the client agreed to.
/// </para>
/// </summary>
public record ContractSnapshot
{
    public required string Reference { get; init; }
    public required DateTime IssuedUtc { get; init; }

    /// <summary>False when any wording came from a template not yet legally approved.</summary>
    public required bool TemplatesApproved { get; init; }

    public required ProviderDetails Provider { get; init; }
    public required BorrowerDetails Borrower { get; init; }
    public required QuoteDetails Quote { get; init; }
    public required IReadOnlyList<ScheduleLine> Schedule { get; init; }
    public required MandateDetails Mandate { get; init; }
    public BudgetDetails? Budget { get; init; }
    public CreditLifeDetails? CreditLife { get; init; }

    public required IReadOnlyList<TermsSection> Terms { get; init; }

    // ---------------------------------------------------------------- serialisation

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// The copy served by a signing link. The ID number is masked to its date-of-birth digits:
    /// signing asks for it, and a forwarded link must not hand an impostor the answer.
    /// </summary>
    public ContractSnapshot ForPublicView() =>
        this with { Borrower = Borrower with { IdNumber = ContractFormat.MaskId(Borrower.IdNumber) } };

    public static ContractSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<ContractSnapshot>(json, Json)
        ?? throw new InvalidOperationException("The contract snapshot could not be read.");

    /// <summary>SHA-256 of the exact JSON stored, hex. Recompute and compare to prove integrity.</summary>
    public static string HashOf(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}

public record ProviderDetails(
    string RegisteredName,
    string? TradingName,
    string? RegistrationNumber,
    string? VatNumber,
    string? NcrNumber,
    string? PhysicalAddress,
    string? PostalAddress,
    string? ContactNumber,
    string? ContactEmail);

public record BorrowerDetails(
    string ClientNumber,
    string FullName,
    string Surname,
    string IdNumber,
    string? Employer,
    string? WorkAddress,
    string? ResidentialAddress,
    string? PostalAddress,
    string? Mobile,
    string? Email);

/// <summary>
/// The quote, in the order the NCA pre-agreement statement sets it out - the same
/// (a)-(l) layout as the lender's current Maxmoney agreement.
/// </summary>
public record QuoteDetails
{
    public required string PackageName { get; init; }
    public required DateTime QuoteDate { get; init; }

    /// <summary>(a) Paid out to the client.</summary>
    public required decimal LoanAmount { get; init; }

    /// <summary>(b) Optional family funeral insurance paid on the client's behalf.</summary>
    public decimal FuneralInsurance { get; init; }

    /// <summary>(c) Optional payment to a third party on the client's behalf.</summary>
    public decimal ThirdPartyPayment { get; init; }

    /// <summary>(d) = (a) + (b) + (c).</summary>
    public decimal TotalLoanAmount => LoanAmount + FuneralInsurance + ThirdPartyPayment;

    /// <summary>(f.1)</summary>
    public required decimal CreditLife { get; init; }

    /// <summary>(f.2)</summary>
    public required decimal InitiationFee { get; init; }

    /// <summary>(f.3)</summary>
    public required decimal ServiceFees { get; init; }

    /// <summary>(f.4)</summary>
    public required decimal Interest { get; init; }

    /// <summary>(f.5) VAT on initiation, service fees and credit life where charged separately.</summary>
    public required decimal Vat { get; init; }

    /// <summary>(e) Total cost of credit = (f.1) to (f.5).</summary>
    public decimal TotalCostOfCredit => CreditLife + InitiationFee + ServiceFees + Interest + Vat;

    /// <summary>(g) = (d) + (e).</summary>
    public decimal TotalRepayable => TotalLoanAmount + TotalCostOfCredit;

    /// <summary>(h) As a fraction: 0.05 is 5% a month.</summary>
    public required decimal MonthlyInterestRate { get; init; }
    public decimal AnnualInterestRate => MonthlyInterestRate * 12m;

    /// <summary>(i) Interest on arrears - by law no more than the agreement rate.</summary>
    public required decimal PenaltyInterestRate { get; init; }

    // (j) Payment schedule
    public required DateTime FirstPaymentDate { get; init; }
    public required DateTime FinalPaymentDate { get; init; }
    public required int NumberOfInstalments { get; init; }
    public required string PaymentMethod { get; init; }
    public required string Frequency { get; init; }
    public required decimal InstalmentAmount { get; init; }
    public decimal FinalInstalmentAmount { get; init; }

    /// <summary>(l) Credit cost multiple = total repayable / total loan amount.</summary>
    public decimal CreditCostMultiple =>
        TotalLoanAmount == 0 ? 0 : Math.Round(TotalRepayable / TotalLoanAmount, 2, MidpointRounding.AwayFromZero);

    /// <summary>True when the rates differ from the package's list price.</summary>
    public bool Customised { get; init; }
}

public record ScheduleLine(
    int Number,
    DateTime DueDate,
    decimal Instalment,
    decimal Interest,
    decimal Capital,
    decimal ServiceFee,
    decimal CreditLife,
    decimal ClosingBalance);

/// <summary>The debit order the client authorises. The account number is masked: see remarks.</summary>
/// <remarks>
/// The pack travels by link. Masking keeps a forwarded link from disclosing a full account
/// number; DebiCheck authentication happens at the client's own bank regardless.
/// </remarks>
public record MandateDetails(
    string? BankName,
    string? BranchCode,
    string? AccountType,
    string? MaskedAccountNumber,
    string? AccountHolder,
    decimal InstalmentAmount,
    decimal TotalAmount,
    int NumberOfInstalments,
    string Frequency,
    string? CollectionDay,
    DateTime? FirstCollectionDate,
    string AgreementReference,
    string StatementReference,
    int TrackingDays);

public record BudgetDetails(
    decimal NetSalary,
    decimal OtherIncome,
    IReadOnlyList<BudgetDetailsLine> Lines,
    decimal DeclaredLivingExpenses,
    decimal StatutoryMinimum,
    decimal DebtInstalments,
    decimal NetOfNet);

public record BudgetDetailsLine(string Description, string Kind, decimal Amount);

public record CreditLifeDetails(
    string? Underwriter,
    string? Administrator,
    decimal MonthlyRate,
    decimal TotalPremium,
    decimal SumAssured,
    DateTime CommencementDate,
    DateTime TerminationDate);

public record TermsSection(string Kind, string Title, string Body);
