
using AMPay.Domain.Entities;
using Microsoft.Extensions.Options;

namespace AMPay.Domain.Credit;

/// <summary>
/// Reducing-balance loan pricing under the National Credit Act short-term credit rules.
/// <para>
/// The shape of the calculation:
/// </para>
/// <list type="number">
/// <item>The initiation fee is charged on the principal and clamped to the statutory
/// maximum, then <em>capitalised</em> - the client receives the full principal and repays
/// principal plus fee.</item>
/// <item>Interest accrues monthly on the reducing capitalised balance. Capital and interest
/// are levelled into a standard annuity instalment.</item>
/// <item>The monthly service fee is flat.</item>
/// <item>Credit life is charged on the <em>opening balance of each month</em>, not on the
/// original advance. This matters: the cap is expressed per R1 000 of the deferred amount
/// at that time, so levelling the premium across the term would overcharge in later months
/// and breach the cap. The consequence is that the instalment declines slightly over the
/// term, which is why the mandate is raised for the first instalment - the largest.</item>
/// </list>
/// </summary>
public class LoanPricingService : ILoanPricingService
{
    private readonly NcaCreditLimits _limits;

    public LoanPricingService(IOptions<NcaCreditLimits> limits) => _limits = limits.Value;

    public LoanQuote Quote(LoanQuoteRequest request, CreditPackage package)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(package);

        if (request.Principal <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The principal must be positive.");

        if (request.NumberOfInstalments <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A loan needs at least one instalment.");

        if (request.Principal < package.MinLoanAmount || request.Principal > package.MaxLoanAmount)
            throw new ArgumentOutOfRangeException(nameof(request),
                $"{package.Name} lends between {Money(package.MinLoanAmount)} and {Money(package.MaxLoanAmount)}.");

        if (request.NumberOfInstalments < package.MinTermMonths ||
            request.NumberOfInstalments > package.MaxTermMonths)
            throw new ArgumentOutOfRangeException(nameof(request),
                $"{package.Name} runs over {package.MinTermMonths} to {package.MaxTermMonths} months.");

        var notices = new List<string>();

        // ---- Rates, clamped to the statutory ceilings ----

        var rate = package.MonthlyInterestRate;
        if (rate > _limits.MonthlyInterestCeiling)
        {
            notices.Add(
                $"Interest reduced from {package.MonthlyInterestRate:P2} to the statutory maximum " +
                $"of {_limits.MonthlyInterestCeiling:P2} per month.");
            rate = _limits.MonthlyInterestCeiling;
        }

        var creditLifeRate = package.CreditLifeRate;
        if (creditLifeRate > _limits.CreditLifeCeilingRate)
        {
            notices.Add(
                $"Credit life reduced from {package.CreditLifeRate:P4} to the statutory maximum " +
                $"of {_limits.CreditLifeCeilingRate:P4} of the deferred amount per month.");
            creditLifeRate = _limits.CreditLifeCeilingRate;
        }

        var serviceFee = package.MonthlyServiceFee;
        if (serviceFee > _limits.MonthlyServiceFeeCeiling)
        {
            notices.Add(
                $"Service fee reduced from {Money(serviceFee)} to the statutory maximum of " +
                $"{Money(_limits.MonthlyServiceFeeCeiling)} per month.");
            serviceFee = _limits.MonthlyServiceFeeCeiling;
        }
        serviceFee = Round(serviceFee);

        // ---- Initiation fee ----

        var requestedFee = Round(request.Principal * package.InitiationFeeRate);
        var statutoryMax = Round(_limits.MaximumInitiationFee(request.Principal));

        var initiationFee = requestedFee;
        if (initiationFee > statutoryMax)
        {
            notices.Add(
                $"Initiation fee reduced from {Money(requestedFee)} to the statutory maximum of " +
                $"{Money(statutoryMax)} for a principal of {Money(request.Principal)}.");
            initiationFee = statutoryMax;
        }

        var capitalised = Round(request.Principal + initiationFee);

        // ---- Level capital-and-interest instalment ----

        var n = request.NumberOfInstalments;
        var baseInstalment = Round(Annuity(capitalised, rate, n));

        // ---- Schedule ----

        var schedule = new List<ScheduleRow>(n);
        var balance = capitalised;

        decimal totalInterest = 0m, totalServiceFees = 0m, totalCreditLife = 0m, totalRepayable = 0m;

        for (var m = 1; m <= n; m++)
        {
            var opening = balance;
            var interest = Round(opening * rate);
            var creditLife = Round(opening * creditLifeRate);

            // The last instalment clears whatever is left, absorbing accumulated rounding.
            var capital = m == n ? opening : Round(baseInstalment - interest);

            // A term short enough that the level instalment does not cover the interest would
            // amortise negatively. Refuse rather than quietly grow the debt.
            if (capital <= 0 && m < n)
                throw new InvalidOperationException(
                    "At this rate and term the instalment does not cover the interest. " +
                    "Shorten the term or reduce the principal.");

            var closing = Round(opening - capital);
            var instalment = Round(capital + interest + serviceFee + creditLife);

            schedule.Add(new ScheduleRow(
                InstalmentNumber: m,
                DueDate: request.FirstCollectionDate.AddMonths(m - 1),
                OpeningBalance: opening,
                InterestPortion: interest,
                CapitalPortion: capital,
                ServiceFee: serviceFee,
                CreditLifePremium: creditLife,
                InstalmentTotal: instalment,
                ClosingBalance: closing));

            totalInterest += interest;
            totalServiceFees += serviceFee;
            totalCreditLife += creditLife;
            totalRepayable += instalment;

            balance = closing;
        }

        return new LoanQuote
        {
            Principal = Round(request.Principal),
            NumberOfInstalments = n,

            MonthlyInterestRate = rate,
            MonthlyServiceFee = serviceFee,
            CreditLifeRate = creditLifeRate,

            InitiationFee = initiationFee,
            CapitalisedAmount = capitalised,

            BaseInstalment = baseInstalment,
            FirstInstalment = schedule[0].InstalmentTotal,
            FinalInstalment = schedule[^1].InstalmentTotal,

            TotalInterest = Round(totalInterest),
            TotalServiceFees = Round(totalServiceFees),
            TotalCreditLife = Round(totalCreditLife),
            TotalRepayable = Round(totalRepayable),
            TotalCostOfCredit = Round(totalRepayable - request.Principal),

            Schedule = schedule,
            Notices = notices
        };
    }

    /// <summary>
    /// Standard annuity payment: P.i.(1+i)^n / ((1+i)^n - 1).
    /// </summary>
    private static decimal Annuity(decimal principal, decimal rate, int periods)
    {
        if (rate == 0m) return principal / periods;

        var growth = Power(1m + rate, periods);
        return principal * rate * growth / (growth - 1m);
    }

    /// <summary>
    /// Integer exponentiation kept in decimal. Going through <see cref="Math.Pow"/> would
    /// round-trip money through a binary double, and at 5% a month over a long term that
    /// drift shows up in the cents of the final instalment.
    /// </summary>
    private static decimal Power(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= value;
        return result;
    }

    private static string Money(decimal value) =>
        "R " + value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
