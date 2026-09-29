using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>
/// Turns engine output into records. Kept out of the controller so the mapping - which is
/// what a signed agreement is ultimately made of - can be tested directly.
/// </summary>
public static class LoanOrigination
{
    public const string LoanNumberPrefix = "LN-";

    /// <summary>
    /// Copies a priced quote onto a loan and returns its schedule rows.
    /// <para>
    /// The rows are returned rather than attached to <c>loan.Schedule</c>. Every entity here
    /// sets its own Guid key, and EF treats a keyed child attached through an already-tracked
    /// parent as an existing row to UPDATE - so the caller adds them through the DbSet.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LoanScheduleEntry> ApplyQuote(Loan loan, LoanQuote quote)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(quote);

        loan.Principal = quote.Principal;
        loan.NumberOfInstalments = quote.NumberOfInstalments;

        // The rates actually used, after any clamping - not the package's list price.
        loan.MonthlyInterestRate = quote.MonthlyInterestRate;
        loan.MonthlyServiceFee = quote.MonthlyServiceFee;
        loan.CreditLifeRate = quote.CreditLifeRate;

        loan.InitiationFee = quote.InitiationFee;
        loan.CapitalisedAmount = quote.CapitalisedAmount;
        loan.BaseInstalment = quote.BaseInstalment;
        loan.FirstInstalment = quote.FirstInstalment;
        loan.FinalInstalment = quote.FinalInstalment;

        loan.TotalInterest = quote.TotalInterest;
        loan.TotalServiceFees = quote.TotalServiceFees;
        loan.TotalCreditLife = quote.TotalCreditLife;
        loan.TotalRepayable = quote.TotalRepayable;
        loan.TotalCostOfCredit = quote.TotalCostOfCredit;

        loan.FirstCollectionDate = quote.Schedule.Count > 0 ? quote.Schedule[0].DueDate : null;

        return quote.Schedule
            .Select(r => new LoanScheduleEntry
            {
                LoanId = loan.Id,
                InstalmentNumber = r.InstalmentNumber,
                DueDate = r.DueDate,
                OpeningBalance = r.OpeningBalance,
                InterestPortion = r.InterestPortion,
                CapitalPortion = r.CapitalPortion,
                ServiceFee = r.ServiceFee,
                CreditLifePremium = r.CreditLifePremium,
                InstalmentTotal = r.InstalmentTotal,
                ClosingBalance = r.ClosingBalance
            })
            .ToList();
    }

    /// <summary>
    /// The affordability inputs for a client, or null when there is no income on file to
    /// assess. Uses net pay only: other income is declared, not evidenced by the payslip.
    /// <para>
    /// <paramref name="ownInstalments"/> is what the client already owes <em>this</em>
    /// lender each month on other approved or disbursed loans. It is added to the debt
    /// declared at onboarding, which was captured before any of our loans existed and so
    /// cannot include them. Leaving it out would assess a second loan as though the first
    /// were not being repaid.
    /// </para>
    /// </summary>
    public static AffordabilityInput? AffordabilityInputFor(
        ClientFinancial? financial, decimal proposedInstalment, decimal ownInstalments = 0m)
    {
        if (financial is null) return null;

        return new AffordabilityInput(
            GrossMonthlyIncome: financial.GrossMonthlyIncome ?? 0m,
            NetMonthlyIncome: financial.NetMonthlyIncome ?? 0m,
            DeclaredMonthlyExpenses: financial.TotalMonthlyExpenses ?? 0m,
            ExistingDebtRepayments: (financial.TotalMonthlyDebtRepayments ?? 0m) + ownInstalments,
            ProposedInstalment: proposedInstalment);
    }

    /// <summary>Snapshots an assessment so the decision can be evidenced later.</summary>
    public static AffordabilityAssessment ToAssessment(
        Guid loanId, AffordabilityInput input, AffordabilityResult result, string? userId) => new()
        {
            LoanId = loanId,
            AssessedByUserId = userId,

            GrossMonthlyIncome = input.GrossMonthlyIncome,
            NetMonthlyIncome = input.NetMonthlyIncome,
            DeclaredMonthlyExpenses = input.DeclaredMonthlyExpenses,
            ExistingDebtRepayments = input.ExistingDebtRepayments,
            StatutoryMinimumExpenses = result.StatutoryMinimumExpenses,
            ProposedInstalment = input.ProposedInstalment,

            DiscretionaryIncome = result.DiscretionaryIncome,
            SurplusAfterInstalment = result.SurplusAfterInstalment,
            UtilisationRatio = result.UtilisationRatio,
            Outcome = result.Outcome,
            Reasoning = result.Reasoning
        };

    /// <summary>
    /// The loan number after <paramref name="highestExisting"/>: LN-000014 gives LN-000015.
    /// Six digits keeps string ordering and numeric ordering the same up to a million loans
    /// per tenant, which is what lets the caller find the highest with a plain ORDER BY.
    /// </summary>
    public static string NextLoanNumber(string? highestExisting)
    {
        var next = 1;

        if (!string.IsNullOrWhiteSpace(highestExisting) &&
            highestExisting.StartsWith(LoanNumberPrefix, StringComparison.Ordinal) &&
            int.TryParse(highestExisting[LoanNumberPrefix.Length..], out var current))
        {
            next = current + 1;
        }

        return $"{LoanNumberPrefix}{next:D6}";
    }

    /// <summary>
    /// A sensible first debit date: the client's next pay day, at least
    /// <paramref name="leadDays"/> away so the mandate has time to be authenticated first.
    /// Without a pay day, the last day of next month.
    /// </summary>
    public static DateTime DefaultFirstCollectionDate(int? salaryDay, DateTime today, int leadDays = 7)
    {
        today = today.Date;

        if (salaryDay is null or < 1 or > 31)
        {
            var nextMonth = today.AddMonths(1);
            return new DateTime(nextMonth.Year, nextMonth.Month,
                DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        }

        var earliest = today.AddDays(leadDays);

        for (var offset = 0; offset < 3; offset++)
        {
            var month = new DateTime(today.Year, today.Month, 1).AddMonths(offset);

            // A pay day of the 31st lands on the last day of shorter months, as payroll does.
            var day = Math.Min(salaryDay.Value, DateTime.DaysInMonth(month.Year, month.Month));
            var candidate = new DateTime(month.Year, month.Month, day);

            if (candidate >= earliest) return candidate;
        }

        return earliest;
    }

    /// <summary>
    /// Netcash collection day ("01".."31", or "LDOM" for the last day of the month).
    /// </summary>
    public static string CollectionDayFor(DateTime firstCollection) =>
        firstCollection.Day == DateTime.DaysInMonth(firstCollection.Year, firstCollection.Month)
            ? "LDOM"
            : firstCollection.Day.ToString("00");
}
