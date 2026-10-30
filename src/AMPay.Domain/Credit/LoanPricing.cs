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

    /// <summary>The ceilings in force. Null uses the statutory defaults.</summary>
    public RegulatoryCaps? Caps { get; init; }

    /// <summary>VAT is charged on the initiation and service fees only when the lender is VAT-registered.</summary>
    public bool VatRegistered { get; init; }

    /// <summary>
    /// The client's short-term loans with this lender earlier in the same calendar year. Decides
    /// the short-term interest ceiling: 5% a month on the first, 3% on later ones.
    /// </summary>
    public int PriorShortTermLoansThisYear { get; init; }

    /// <summary>(b) Optional family funeral cover the client opted into, paid on their behalf.</summary>
    public decimal FuneralCover { get; init; }

    /// <summary>(c) Optional payment to a third party on the client's behalf.</summary>
    public decimal ThirdPartyPayment { get; init; }

    /// <summary>A permitted user's change to this one loan's pricing, at or below the caps.</summary>
    public FeeOverride? Override { get; init; }
}

/// <summary>
/// Changes to a single loan's pricing by a user with the fee-override permission. Anything above
/// the cap is refused outright (the only tolerance is a cent for rounding); every override is
/// recorded with its reason.
/// </summary>
public record FeeOverride(decimal? InitiationFee, decimal? MonthlyServiceFee, decimal? MonthlyInterestRate, string Reason);

/// <summary>One priced instalment. Amounts on fees exclude VAT; the VAT is its own column.</summary>
public record ScheduleRow(
    int InstalmentNumber,
    DateTime DueDate,
    decimal OpeningBalance,
    decimal InterestPortion,
    decimal CapitalPortion,
    decimal ServiceFee,
    decimal CreditLifePremium,
    decimal InstalmentTotal,
    decimal ClosingBalance,
    decimal VatPortion = 0m);

/// <summary>
/// A fully priced loan. Immutable - the engine does not mutate a <see cref="Loan"/>,
/// it hands back a quote that the caller chooses to persist.
/// <para>
/// The cost elements follow the a-l lettering of the credit agreement: (a) principal, (b) funeral
/// cover, (c) third-party payment, (d) total loan amount, (f.1-f.5) credit life, initiation fee,
/// service fees, interest, VAT, (e) total cost of credit, (g) total repayable.
/// </para>
/// </summary>
public record LoanQuote
{
    public required decimal Principal { get; init; }
    public required int NumberOfInstalments { get; init; }

    /// <summary>"Short-term Credit Transaction" or "Unsecured Credit Transaction".</summary>
    public string ProductType { get; init; } = "";

    public required decimal MonthlyInterestRate { get; init; }
    public required decimal MonthlyServiceFee { get; init; }
    public required decimal CreditLifeRate { get; init; }

    /// <summary>Excluding VAT.</summary>
    public required decimal InitiationFee { get; init; }

    /// <summary>Principal, optional extras, initiation fee and its VAT: the balance interest runs on.</summary>
    public required decimal CapitalisedAmount { get; init; }

    public required decimal BaseInstalment { get; init; }
    public required decimal FirstInstalment { get; init; }
    public required decimal FinalInstalment { get; init; }

    public required decimal TotalInterest { get; init; }
    public required decimal TotalServiceFees { get; init; }
    public required decimal TotalCreditLife { get; init; }
    public required decimal TotalRepayable { get; init; }
    public required decimal TotalCostOfCredit { get; init; }

    // ---- the a-l items not already above ----

    public decimal FuneralCover { get; init; }
    public decimal ThirdPartyPayment { get; init; }
    /// <summary>(d) a + b + c.</summary>
    public decimal TotalLoanAmount { get; init; }
    /// <summary>(f.5) VAT on the initiation and service fees. Credit life is VAT-exempt.</summary>
    public decimal TotalVat { get; init; }
    public decimal VatRate { get; init; }
    /// <summary>(i) Penalty interest: equal to the contract rate.</summary>
    public decimal PenaltyMonthlyRate { get; init; }
    /// <summary>(l) Total repayable over the total loan amount, two decimals.</summary>
    public decimal CreditCostMultiple { get; init; }
    /// <summary>The interest ceiling the rate was checked against.</summary>
    public decimal InterestCeiling { get; init; }
    /// <summary>The override applied, if any - for the audit trail.</summary>
    public FeeOverride? AppliedOverride { get; init; }

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
    /// The principal or term falls outside the package limits, or an override exceeds a cap.
    /// </exception>
    /// <exception cref="InvalidOperationException">A cap needed for this loan is not set.</exception>
    LoanQuote Quote(LoanQuoteRequest request, CreditPackage package);
}
