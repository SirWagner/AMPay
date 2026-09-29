using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>What the operator supplies. Everything else is calculated.</summary>
public record LoanQuoteRequest(
    decimal Principal,
    int NumberOfInstalments,
    DateTime FirstCollectionDate)
{
    /// <summary>Set when repricing an existing agreement rather than quoting a new one.</summary>
    public DateTime? AgreementDate { get; init; }
}

/// <summary>One priced instalment.</summary>
public record ScheduleRow(
    int InstalmentNumber,
    DateTime DueDate,
    decimal OpeningBalance,
    decimal InterestPortion,
    decimal CapitalPortion,
    decimal ServiceFee,
    decimal CreditLifePremium,
    decimal InstalmentTotal,
    decimal ClosingBalance);

/// <summary>
/// A fully priced loan. Immutable - the engine does not mutate a <see cref="Loan"/>,
/// it hands back a quote that the caller chooses to persist.
/// </summary>
public record LoanQuote
{
    public required decimal Principal { get; init; }
    public required int NumberOfInstalments { get; init; }

    public required decimal MonthlyInterestRate { get; init; }
    public required decimal MonthlyServiceFee { get; init; }
    public required decimal CreditLifeRate { get; init; }

    public required decimal InitiationFee { get; init; }
    public required decimal CapitalisedAmount { get; init; }

    public required decimal BaseInstalment { get; init; }
    public required decimal FirstInstalment { get; init; }
    public required decimal FinalInstalment { get; init; }

    public required decimal TotalInterest { get; init; }
    public required decimal TotalServiceFees { get; init; }
    public required decimal TotalCreditLife { get; init; }
    public required decimal TotalRepayable { get; init; }
    public required decimal TotalCostOfCredit { get; init; }

    public required IReadOnlyList<ScheduleRow> Schedule { get; init; }

    /// <summary>
    /// Anything the quote had to clamp or could not honour - an initiation fee reduced to
    /// the statutory maximum, a rate above the cap. Always surfaced to the operator.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Prices a loan under a credit package. The single place loan arithmetic lives.
/// </summary>
public interface ILoanPricingService
{
    /// <summary>
    /// Prices <paramref name="request"/> against <paramref name="package"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The principal or term falls outside the package limits.
    /// </exception>
    LoanQuote Quote(LoanQuoteRequest request, CreditPackage package);
}
