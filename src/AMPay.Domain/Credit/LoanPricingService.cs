using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>
/// Reducing-balance loan pricing under the National Credit Act, with the cost elements laid out
/// as the credit agreement prints them (a-l).
/// <para>
/// The shape of the calculation:
/// </para>
/// <list type="number">
/// <item>All package fees and the statutory caps are EXCLUDING VAT. A VAT-registered lender adds
/// VAT on the initiation and service fees (credit life is VAT-exempt); that is item f.5.</item>
/// <item>The initiation fee (and its VAT) is <em>capitalised</em> with the principal and any
/// optional funeral cover or third-party payment: the client receives the full principal.</item>
/// <item>Interest accrues monthly on the reducing balance. Credit life is charged on each month's
/// opening balance, because its cap is per R1 000 outstanding at the time.</item>
/// <item>Instalments are level: every instalment is the same amount except the last, which
/// absorbs the rounding cents. So the collection amount is one figure, and instalments added up
/// equal the total repayable to the cent (g = d + e).</item>
/// <item>Rates and fees above a cap are reduced to it, with a notice. An override above a cap is
/// refused outright.</item>
/// </list>
/// </summary>
public class LoanPricingService : ILoanPricingService
{
    /// <summary>The only tolerance above a cap: a cent, for rounding.</summary>
    public const decimal CapTolerance = 0.01m;

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

        if (request.FuneralCover < 0 || request.ThirdPartyPayment < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Optional amounts cannot be negative.");

        var caps = request.Caps ?? RegulatoryCaps.Statutory;
        var n = request.NumberOfInstalments;
        var shortTerm = caps.IsShortTerm(request.Principal, n);
        var overrides = request.Override;
        var notices = new List<string>();

        // ---- Interest ----

        var ceiling = caps.MonthlyInterestCeiling(request.Principal, n, request.PriorShortTermLoansThisYear)
                      ?? throw new InvalidOperationException(
                          "This is an unsecured credit transaction (over " + Money(caps.ShortTermMaxPrincipal) + " or longer than " +
                          caps.ShortTermMaxMonths + " months) and the unsecured interest cap is not set. " +
                          "A platform administrator sets it under Regulatory caps.");

        var rate = Pick(overrides?.MonthlyInterestRate, package.MonthlyInterestRate, ceiling, "Interest", notices,
            v => v.ToString("P2"), whatFor: shortTerm && request.PriorShortTermLoansThisYear > 0
                ? " (a later short-term loan this calendar year)" : "");

        // ---- Credit life ----

        var creditLifeRate = package.CreditLifeRate;
        if (creditLifeRate > caps.CreditLifeMonthlyRateMax)
        {
            notices.Add(
                $"Credit life reduced from {package.CreditLifeRate:P4} to the statutory maximum " +
                $"of {caps.CreditLifeMonthlyRateMax:P4} of the balance per month.");
            creditLifeRate = caps.CreditLifeMonthlyRateMax;
        }

        // ---- Fees (excluding VAT) ----

        var serviceFee = Round(Pick(overrides?.MonthlyServiceFee, package.MonthlyServiceFee, caps.MonthlyServiceFeeMax,
            "Service fee", notices, Money, suffix: " per month"));

        var initiationMax = Round(caps.MaximumInitiationFee(request.Principal));
        var initiationFee = Round(Pick(overrides?.InitiationFee, Round(request.Principal * package.InitiationFeeRate), initiationMax,
            "Initiation fee", notices, Money, suffix: $" for a principal of {Money(request.Principal)}"));

        var vatRate = request.VatRegistered ? caps.VatRate : 0m;
        var initiationVat = Round(initiationFee * vatRate);
        var serviceFeeVat = Round(serviceFee * vatRate);

        // ---- What is financed ----

        var totalLoanAmount = Round(request.Principal + request.FuneralCover + request.ThirdPartyPayment); // (d)
        var capitalised = Round(totalLoanAmount + initiationFee + initiationVat);

        // ---- Level instalment and schedule ----

        var instalment = LevelInstalment(capitalised, rate, creditLifeRate, serviceFee + serviceFeeVat, n);
        var schedule = Schedule(request.FirstCollectionDate, capitalised, rate, creditLifeRate, serviceFee, serviceFeeVat, n, instalment);

        var totalInterest = schedule.Sum(r => r.InterestPortion);
        var totalServiceFees = schedule.Sum(r => r.ServiceFee);
        var totalCreditLife = schedule.Sum(r => r.CreditLifePremium);
        var totalVat = initiationVat + schedule.Sum(r => r.VatPortion);
        var totalRepayable = schedule.Sum(r => r.InstalmentTotal);                          // (g)
        var totalCostOfCredit = Round(totalCreditLife + initiationFee + totalServiceFees + totalInterest + totalVat); // (e)

        if (totalRepayable != totalLoanAmount + totalCostOfCredit)
            throw new InvalidOperationException("The cost of credit does not add up to the total repayable."); // g = d + e, to the cent

        return new LoanQuote
        {
            Principal = Round(request.Principal),
            NumberOfInstalments = n,
            ProductType = shortTerm ? "Short-term Credit Transaction" : "Unsecured Credit Transaction",

            MonthlyInterestRate = rate,
            MonthlyServiceFee = serviceFee,
            CreditLifeRate = creditLifeRate,

            InitiationFee = initiationFee,
            CapitalisedAmount = capitalised,

            BaseInstalment = instalment,
            FirstInstalment = schedule[0].InstalmentTotal,
            FinalInstalment = schedule[^1].InstalmentTotal,

            TotalInterest = Round(totalInterest),
            TotalServiceFees = Round(totalServiceFees),
            TotalCreditLife = Round(totalCreditLife),
            TotalRepayable = Round(totalRepayable),
            TotalCostOfCredit = totalCostOfCredit,

            FuneralCover = Round(request.FuneralCover),
            ThirdPartyPayment = Round(request.ThirdPartyPayment),
            TotalLoanAmount = totalLoanAmount,
            TotalVat = Round(totalVat),
            VatRate = vatRate,
            PenaltyMonthlyRate = rate,
            CreditCostMultiple = Math.Round(totalRepayable / totalLoanAmount, 2, MidpointRounding.AwayFromZero),
            InterestCeiling = ceiling,
            AppliedOverride = overrides,

            Schedule = schedule,
            Notices = notices
        };
    }

    /// <summary>
    /// The override when one is given (refused above the cap), otherwise the package figure
    /// (reduced to the cap with a notice).
    /// </summary>
    private static decimal Pick(decimal? overridden, decimal packageValue, decimal cap, string what, List<string> notices,
        Func<decimal, string> show, string suffix = "", string whatFor = "")
    {
        if (overridden is { } o)
        {
            if (o < 0)
                throw new ArgumentOutOfRangeException(nameof(overridden), $"{what} cannot be negative.");
            if (o > cap + CapTolerance)
                throw new ArgumentOutOfRangeException(nameof(overridden),
                    $"{what} of {show(o)} is above the statutory maximum of {show(cap)}{suffix}{whatFor}. Overrides go up to the cap, not past it.");
            return o;
        }

        if (packageValue <= cap) return packageValue;

        notices.Add($"{what} reduced from {show(packageValue)} to the statutory maximum of {show(cap)}{suffix}{whatFor}.");
        return cap;
    }

    /// <summary>
    /// The level instalment that clears <paramref name="capitalised"/> in <paramref name="n"/>
    /// payments when each month's interest and credit life are charged on its opening balance and
    /// <paramref name="monthlyFees"/> (service fee with its VAT) is added. Found by bisection, then
    /// rounded to the cent; the final instalment absorbs what is left.
    /// </summary>
    private static decimal LevelInstalment(decimal capitalised, decimal rate, decimal creditLifeRate, decimal monthlyFees, int n)
    {
        decimal Remaining(decimal payment)
        {
            var balance = capitalised;
            for (var m = 1; m < n; m++)
                balance -= payment - Round(balance * rate) - Round(balance * creditLifeRate) - monthlyFees;
            return balance;
        }

        // What is left for the final instalment must equal what one level instalment pays off.
        decimal Gap(decimal payment)
        {
            var last = Remaining(payment);
            return last + Round(last * rate) + Round(last * creditLifeRate) + monthlyFees - payment;
        }

        decimal lo = 0m, hi = capitalised * 2m + monthlyFees * n + 1m;
        for (var i = 0; i < 80 && hi - lo > 0.0001m; i++)
        {
            var mid = (lo + hi) / 2m;
            if (Gap(mid) > 0) lo = mid; else hi = mid;
        }
        return Round(hi);
    }

    private static List<ScheduleRow> Schedule(DateTime firstDue, decimal capitalised, decimal rate, decimal creditLifeRate,
        decimal serviceFee, decimal serviceFeeVat, int n, decimal instalment)
    {
        var rows = new List<ScheduleRow>(n);
        var balance = capitalised;

        for (var m = 1; m <= n; m++)
        {
            var opening = balance;
            var interest = Round(opening * rate);
            var creditLife = Round(opening * creditLifeRate);
            var charges = interest + creditLife + serviceFee + serviceFeeVat;

            // The last instalment clears whatever is left, absorbing the rounding.
            var capital = m == n ? opening : Round(instalment - charges);

            // A term too short for the instalment to cover the charges would grow the debt.
            if (capital <= 0 && m < n)
                throw new InvalidOperationException(
                    "At this rate and term the instalment does not cover the interest and fees. " +
                    "Shorten the term or reduce the principal.");

            var closing = Round(opening - capital);
            rows.Add(new ScheduleRow(
                InstalmentNumber: m,
                DueDate: firstDue.AddMonths(m - 1),
                OpeningBalance: opening,
                InterestPortion: interest,
                CapitalPortion: capital,
                ServiceFee: serviceFee,
                CreditLifePremium: creditLife,
                InstalmentTotal: Round(capital + charges),
                ClosingBalance: closing,
                VatPortion: serviceFeeVat));

            balance = closing;
        }

        return rows;
    }

    private static string Money(decimal value) =>
        "R " + value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
