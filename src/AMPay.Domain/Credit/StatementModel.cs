using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>
/// What a staff member reads off a client's bank statement. Counts and monthly averages only:
/// nothing here needs the statement to be machine-readable, so a photographed or printed
/// statement works as well as a PDF.
/// </summary>
public record StatementFigures
{
    /// <summary>Whole months the statement covers.</summary>
    public int MonthsCovered { get; init; }

    /// <summary>Salary or wage deposits seen in that period.</summary>
    public int SalaryDeposits { get; init; }

    /// <summary>The average of those deposits: what really lands in the account each pay day.</summary>
    public decimal AverageSalaryDeposit { get; init; }

    /// <summary>Debit orders the bank returned unpaid, over the whole period.</summary>
    public int ReturnedDebitOrders { get; init; }

    /// <summary>Different lenders being repaid from this account (other than this lender).</summary>
    public int OtherLenders { get; init; }

    /// <summary>What goes to those lenders in an average month.</summary>
    public decimal MonthlyLoanRepayments { get; init; }

    /// <summary>Betting, casino and lottery spend in an average month.</summary>
    public decimal MonthlyGambling { get; init; }

    /// <summary>Days the balance was below zero, over the whole period.</summary>
    public int DaysOverdrawn { get; init; }
}

public record StatementVerdict(StatementFlag Flag, IReadOnlyList<GoodToGoCheck> Findings, string Summary)
{
    public string SerialisedFindings => string.Join('\n', Findings.Select(f => f.Serialise()));
}

/// <summary>
/// The bank-statement model: rules, not AI. It validates what the client declared (income and
/// debt instalments) against what the statement shows, and looks for the signs of someone who
/// cannot keep up: bounced debit orders, several lenders, gambling, living below zero. The
/// worst finding sets the flag - Good, Warning or Bad.
/// <para>
/// House rules, not statute. Every threshold is a named constant here, with a test, so changing
/// a lender's appetite is a small reviewed change rather than a guess buried in a screen.
/// </para>
/// </summary>
public static class StatementModel
{
    /// <summary>Statements should cover this many months to show a pattern.</summary>
    public const int MonthsWanted = 3;

    /// <summary>Statement income this far below the declared income is a warning (fraction).</summary>
    public const decimal IncomeShortfallWarning = 0.10m;

    /// <summary>...and this far below, the declared income is not borne out at all.</summary>
    public const decimal IncomeShortfallBad = 0.25m;

    /// <summary>Loan repayments on the statement above the declared instalments by this fraction: undeclared debt.</summary>
    public const decimal UndeclaredDebtWarning = 0.10m;
    public const decimal UndeclaredDebtBad = 0.50m;

    /// <summary>Differences smaller than this (rand) are rounding, not findings.</summary>
    public const decimal Tolerance = 100m;

    /// <summary>This many other lenders being repaid at once is a debt spiral.</summary>
    public const int ManyLenders = 3;

    /// <summary>Returned debit orders, over the period, that make the statement Bad.</summary>
    public const int ReturnedDebitsBad = 3;

    /// <summary>Gambling above these shares of income is a warning, then bad.</summary>
    public const decimal GamblingWarning = 0.02m;
    public const decimal GamblingBad = 0.10m;

    /// <summary>Days below zero in an average month that make the statement Bad.</summary>
    public const int OverdrawnDaysPerMonthBad = 5;

    public static StatementVerdict Evaluate(StatementFigures f, decimal? declaredNetIncome, decimal declaredDebtInstalments)
    {
        ArgumentNullException.ThrowIfNull(f);
        var findings = new List<GoodToGoCheck>();
        void Add(CheckStatus s, string name, string detail) => findings.Add(new GoodToGoCheck(s, name, detail));
        var months = Math.Max(f.MonthsCovered, 1);

        // ---- how much statement there is
        if (f.MonthsCovered < MonthsWanted)
            Add(CheckStatus.Warn, "Period", $"Only {f.MonthsCovered} month(s) of statements. Ask for {MonthsWanted} to see a pattern.");
        else
            Add(CheckStatus.Pass, "Period", $"{f.MonthsCovered} months of statements.");

        // ---- is there a salary, and does it come every month
        if (f.SalaryDeposits <= 0 || f.AverageSalaryDeposit <= 0)
            Add(CheckStatus.Fail, "Salary", "No salary or wage deposit on the statement.");
        else if (f.SalaryDeposits < f.MonthsCovered)
            Add(CheckStatus.Warn, "Salary", $"{f.SalaryDeposits} salary deposit(s) in {f.MonthsCovered} months: income is not regular.");
        else
            Add(CheckStatus.Pass, "Salary", $"Paid regularly: {f.SalaryDeposits} deposit(s) in {f.MonthsCovered} months.");

        // ---- the declared income, validated against what lands in the account
        if (f.AverageSalaryDeposit > 0)
        {
            if (declaredNetIncome is not > 0)
                Add(CheckStatus.Missing, "Declared income", "No net income captured to compare with. Capture it on the Financial step.");
            else
            {
                var declared = declaredNetIncome.Value;
                var gap = declared - f.AverageSalaryDeposit;
                var share = gap / declared;
                if (gap > Tolerance && share > IncomeShortfallBad)
                    Add(CheckStatus.Fail, "Declared income",
                        $"Declared R {declared:N2} but the statement shows R {f.AverageSalaryDeposit:N2} a month: R {gap:N2} ({share:P0}) less.");
                else if (gap > Tolerance && share > IncomeShortfallWarning)
                    Add(CheckStatus.Warn, "Declared income",
                        $"Declared R {declared:N2}; the statement shows R {f.AverageSalaryDeposit:N2} a month, R {gap:N2} ({share:P0}) less. Use the lower figure.");
                else
                    Add(CheckStatus.Pass, "Declared income",
                        $"Declared R {declared:N2}; the statement shows R {f.AverageSalaryDeposit:N2} a month. It agrees.");
            }
        }

        // ---- debt: what was declared against what is being repaid
        var extra = f.MonthlyLoanRepayments - declaredDebtInstalments;
        if (f.MonthlyLoanRepayments <= 0 && f.OtherLenders <= 0)
            Add(CheckStatus.Pass, "Other lenders", "No repayments to other lenders on the statement.");
        else if (extra > Tolerance && extra > declaredDebtInstalments * UndeclaredDebtBad)
            Add(CheckStatus.Fail, "Other lenders",
                $"Repaying R {f.MonthlyLoanRepayments:N2} a month to {Lenders(f.OtherLenders)} but declared only R {declaredDebtInstalments:N2}: about R {extra:N2} a month of debt was not declared. Add it to the budget and check again.");
        else if (extra > Tolerance && extra > declaredDebtInstalments * UndeclaredDebtWarning)
            Add(CheckStatus.Warn, "Other lenders",
                $"Repaying R {f.MonthlyLoanRepayments:N2} a month to {Lenders(f.OtherLenders)}; R {declaredDebtInstalments:N2} was declared. Add the difference to the budget.");
        else if (f.OtherLenders >= ManyLenders)
            Add(CheckStatus.Warn, "Other lenders", $"Repaying {f.OtherLenders} other lenders at once (R {f.MonthlyLoanRepayments:N2} a month), all declared.");
        else
            Add(CheckStatus.Pass, "Other lenders", $"R {f.MonthlyLoanRepayments:N2} a month to {Lenders(f.OtherLenders)}, as declared.");

        // ---- signs of not keeping up
        if (f.ReturnedDebitOrders >= ReturnedDebitsBad)
            Add(CheckStatus.Fail, "Returned debit orders", $"{f.ReturnedDebitOrders} debit orders returned unpaid: a DebiCheck collection is likely to bounce too.");
        else if (f.ReturnedDebitOrders > 0)
            Add(CheckStatus.Warn, "Returned debit orders", $"{f.ReturnedDebitOrders} debit order(s) returned unpaid. Ask what happened.");
        else
            Add(CheckStatus.Pass, "Returned debit orders", "None.");

        var income = f.AverageSalaryDeposit > 0 ? f.AverageSalaryDeposit : declaredNetIncome ?? 0m;
        if (f.MonthlyGambling <= 0)
            Add(CheckStatus.Pass, "Gambling", "None.");
        else if (income <= 0 || f.MonthlyGambling > income * GamblingBad)
            Add(CheckStatus.Fail, "Gambling", $"R {f.MonthlyGambling:N2} a month on gambling{(income > 0 ? $", {f.MonthlyGambling / income:P0} of income" : "")}.");
        else if (f.MonthlyGambling > income * GamblingWarning)
            Add(CheckStatus.Warn, "Gambling", $"R {f.MonthlyGambling:N2} a month on gambling, {f.MonthlyGambling / income:P0} of income.");
        else
            Add(CheckStatus.Pass, "Gambling", $"R {f.MonthlyGambling:N2} a month: small against income.");

        if (f.DaysOverdrawn <= 0)
            Add(CheckStatus.Pass, "Balance", "Never below zero.");
        else if (f.DaysOverdrawn >= OverdrawnDaysPerMonthBad * months)
            Add(CheckStatus.Fail, "Balance", $"Below zero on {f.DaysOverdrawn} days in {f.MonthsCovered} month(s): living on overdraft.");
        else
            Add(CheckStatus.Warn, "Balance", $"Below zero on {f.DaysOverdrawn} day(s). The money runs out before pay day.");

        var flag = findings.Any(x => x.Status == CheckStatus.Fail) ? StatementFlag.Bad
            : findings.Any(x => x.Status is CheckStatus.Warn or CheckStatus.Missing) ? StatementFlag.Warning
            : StatementFlag.Good;

        var problems = findings.Where(x => x.Status is CheckStatus.Fail or CheckStatus.Warn).ToList();
        var summary = flag switch
        {
            StatementFlag.Good => "The statement agrees with what the client declared and shows nothing of concern.",
            StatementFlag.Warning => "Worth a conversation: " + string.Join("; ", problems.Select(p => p.Name.ToLowerInvariant())) + ".",
            _ => "The statement does not support this loan: " +
                 string.Join("; ", problems.Where(p => p.Status == CheckStatus.Fail).Select(p => p.Name.ToLowerInvariant())) + "."
        };

        return new StatementVerdict(flag, findings, summary);
    }

    private static string Lenders(int n) => n == 1 ? "1 other lender" : $"{n} other lenders";
}
