using AMPay.Domain.Enums;

namespace AMPay.Domain.Credit;

/// <summary>
/// The Regulation 23A(7) minimum expense norms: the least a household in a given income
/// band is assumed to spend, whatever the client declares.
/// <para>
/// VERIFY BEFORE GO-LIVE. Like <see cref="NcaCreditLimits"/>, these are gazetted figures
/// that get amended. They are configuration so that an amendment is a settings edit, but
/// the defaults still need checking against the current Regulations.
/// </para>
/// </summary>
public class AffordabilityNorms
{
    public const string SectionName = "Affordability";

    /// <summary>
    /// A loan whose instalment consumes more than this share of discretionary income is
    /// flagged Marginal rather than passed outright. Not statutory - a house risk appetite.
    /// </summary>
    public decimal MarginalUtilisationThreshold { get; set; } = 0.75m;

    public List<ExpenseBand> Bands { get; set; } = new()
    {
        // Below this income there is no discretionary income to speak of: the whole of it
        // is treated as minimum living expenses.
        new ExpenseBand { UpperBound = 800m,    Base = 0m,        MarginalRate = 1.00m,   BandFloor = 0m },
        new ExpenseBand { UpperBound = 6_250m,  Base = 800m,      MarginalRate = 0.0675m, BandFloor = 800m },
        new ExpenseBand { UpperBound = 25_000m, Base = 1_167.88m, MarginalRate = 0.0900m, BandFloor = 6_250m },
        new ExpenseBand { UpperBound = 50_000m, Base = 2_855.38m, MarginalRate = 0.0825m, BandFloor = 25_000m },
        new ExpenseBand { UpperBound = null,    Base = 4_918.51m, MarginalRate = 0.0675m, BandFloor = 50_000m }
    };

    /// <summary>
    /// The statutory minimum monthly expense for a given gross monthly income.
    /// </summary>
    public decimal MinimumExpensesFor(decimal grossMonthlyIncome)
    {
        if (grossMonthlyIncome <= 0) return 0m;

        foreach (var band in Bands.OrderBy(b => b.UpperBound ?? decimal.MaxValue))
        {
            if (band.UpperBound is not null && grossMonthlyIncome > band.UpperBound) continue;

            var minimum = band.Base + (grossMonthlyIncome - band.BandFloor) * band.MarginalRate;

            // Cents, like every other money figure here. The bands are continuous once
            // rounded: at R6 250 the second band gives 1 167.875, which is the third
            // band's base.
            return Math.Round(minimum, 2, MidpointRounding.AwayFromZero);
        }

        return grossMonthlyIncome;
    }

    public class ExpenseBand
    {
        /// <summary>Top of the band. Null means the band is open-ended.</summary>
        public decimal? UpperBound { get; set; }

        /// <summary>Flat component for this band.</summary>
        public decimal Base { get; set; }

        /// <summary>Applied to income above <see cref="BandFloor"/>.</summary>
        public decimal MarginalRate { get; set; }

        /// <summary>Income level at which this band starts.</summary>
        public decimal BandFloor { get; set; }
    }
}

/// <summary>
/// Inputs to an affordability assessment.
/// <para>
/// <paramref name="OtherMonthlyIncome"/> is income beyond the payslip - the "Other Income"
/// line of the Maxmoney budget. It counts towards both gross income (and so the client's
/// Regulation 23A band) and net income: it is not taxed through payroll, so there is no
/// separate net figure for it.
/// </para>
/// </summary>
public record AffordabilityInput(
    decimal GrossMonthlyIncome,
    decimal NetMonthlyIncome,
    decimal DeclaredMonthlyExpenses,
    decimal ExistingDebtRepayments,
    decimal ProposedInstalment,
    decimal OtherMonthlyIncome = 0m)
{
    public decimal TotalGrossIncome => GrossMonthlyIncome + OtherMonthlyIncome;
    public decimal TotalNetIncome => NetMonthlyIncome + OtherMonthlyIncome;
}

/// <summary>The assessment, with its reasoning.</summary>
public record AffordabilityResult
{
    public required AffordabilityOutcome Outcome { get; init; }

    public required decimal StatutoryMinimumExpenses { get; init; }

    /// <summary>The expense figure actually used: the greater of declared and statutory.</summary>
    public required decimal AppliedExpenses { get; init; }

    public required decimal DiscretionaryIncome { get; init; }
    public required decimal SurplusAfterInstalment { get; init; }

    /// <summary>Instalment as a fraction of discretionary income. Zero income gives 0.</summary>
    public required decimal UtilisationRatio { get; init; }

    public required string Reasoning { get; init; }

    /// <summary>The largest instalment this client could service. Zero when none.</summary>
    public required decimal MaximumAffordableInstalment { get; init; }
}

/// <summary>
/// Runs the NCA s78-81 affordability test. Separate from pricing: pricing says what a loan
/// costs, this says whether the client may be given it.
/// </summary>
public interface IAffordabilityService
{
    AffordabilityResult Assess(AffordabilityInput input);
}
