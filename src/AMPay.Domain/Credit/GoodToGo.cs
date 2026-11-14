using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>
/// Everything the good-to-go check looks at. All of it is already on the client's file: the
/// check makes no paid call, which is the point - it runs first, and the credit bureau report,
/// which is paid for, is the final check only for clients who pass it.
/// </summary>
public record GoodToGoInput
{
    public bool IsSaIdNumber { get; init; } = true;
    public bool IdNumberValid { get; init; }
    public int? AgeYears { get; init; }

    public bool PopiaConsent { get; init; }
    public bool HasMobileNumber { get; init; }

    public string? EmploymentType { get; init; }
    public DateTime? EmployedSince { get; init; }
    public DateTime? ContractExpiryDate { get; init; }

    public decimal? NetMonthlyIncome { get; init; }
    public bool BudgetCaptured { get; init; }

    /// <summary>What is left each month after expenses (Regulation 23A minimum applied) and debt. Null until income is in.</summary>
    public decimal? NetOfNet { get; init; }

    public bool HasBankAccount { get; init; }
    public bool BankAccountVerified { get; init; }

    public VerificationResult? LifeVerification { get; init; }

    /// <summary>The Home Affairs answer came from the stub: no provider is connected, nothing was really checked.</summary>
    public bool LifeVerificationSimulated { get; init; }
    public VerificationResult? EmploymentVerification { get; init; }

    /// <summary>The latest AI bank-statement flag, when a statement has really been assessed (a simulated one says nothing).</summary>
    public StatementFlag? StatementFlag { get; init; }

    // History with this lender.
    public int PreviousLoans { get; init; }
    public int ActiveLoans { get; init; }
    public int WrittenOffLoans { get; init; }

    /// <summary>Days the oldest unpaid instalment on any of the client's loans is overdue. 0 when up to date.</summary>
    public int DaysInArrears { get; init; }

    public DateTime Today { get; init; } = DateTime.UtcNow.Date;
}

public enum CheckStatus
{
    Pass = 0,
    /// <summary>Not a reason to refuse, but a person should look before the credit check.</summary>
    Warn = 1,
    /// <summary>Not captured yet.</summary>
    Missing = 2,
    /// <summary>The lender does not lend past this.</summary>
    Fail = 3
}

public record GoodToGoCheck(CheckStatus Status, string Name, string Detail)
{
    /// <summary>"Fail|Employment|Unemployed" - how a check is kept on <see cref="ClientScreening.Checks"/>.</summary>
    public string Serialise() => $"{Status}|{Name}|{Detail.Replace('\n', ' ').Replace('|', '/')}";

    public static GoodToGoCheck? Parse(string line)
    {
        var parts = line.Split('|', 3);
        return parts.Length == 3 && Enum.TryParse<CheckStatus>(parts[0], out var s)
            ? new GoodToGoCheck(s, parts[1], parts[2])
            : null;
    }
}

public record GoodToGoResult(ScreeningOutcome Outcome, int Score, bool IsFirstTimeApplicant, IReadOnlyList<GoodToGoCheck> Checks)
{
    public string SerialisedChecks => string.Join('\n', Checks.Select(c => c.Serialise()));

    public static IReadOnlyList<GoodToGoCheck> ParseChecks(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(GoodToGoCheck.Parse).OfType<GoodToGoCheck>().ToList();
}

/// <summary>
/// The lender's "good to go" pre-screen. Any failed check makes the client Not good to go; anything
/// missing or worth a second look makes it Review; otherwise Good to go. The score is 100 less a
/// penalty per check that is not a pass - indicative, for sorting and for the client report.
/// <para>
/// House rules, not statute. The thresholds are here in one place so that a lender's change of
/// appetite is a small, reviewed code change with tests, not a setting anyone can flip.
/// </para>
/// </summary>
public static class GoodToGo
{
    public const int MinimumAge = 18;

    /// <summary>From this age credit life cover gets scarce and dear: worth a look, not a refusal.</summary>
    public const int ReviewFromAge = 65;

    /// <summary>Fewer months than this in the job is a warning: probation periods are usually three months.</summary>
    public const int MinimumMonthsEmployed = 3;

    /// <summary>A fixed-term contract ending within this many months is a warning.</summary>
    public const int ContractEndingWithinMonths = 3;

    /// <summary>Less than this left each month (rand) is too thin to carry an instalment comfortably.</summary>
    public const decimal ThinNetOfNet = 500m;

    /// <summary>Days overdue on an existing loan that stop a new one.</summary>
    public const int ArrearsRefusalDays = 30;

    public static readonly string[] EmploymentTypes =
        { "Permanent", "Contract", "Temporary", "Self-employed", "Pensioner", "Unemployed" };

    public static GoodToGoResult Evaluate(GoodToGoInput x)
    {
        var checks = new List<GoodToGoCheck>();
        void Add(CheckStatus s, string name, string detail) => checks.Add(new GoodToGoCheck(s, name, detail));

        // ---- identity
        if (!x.IsSaIdNumber)
            Add(CheckStatus.Warn, "Identity", "Passport, not an SA ID: Home Affairs and the bureaus cannot check it the usual way.");
        else if (!x.IdNumberValid)
            Add(CheckStatus.Fail, "Identity", "The SA ID number is not valid.");
        else
            Add(CheckStatus.Pass, "Identity", "Valid SA ID number.");

        if (x.AgeYears is null)
            Add(CheckStatus.Missing, "Age", "Date of birth not captured.");
        else if (x.AgeYears < MinimumAge)
            Add(CheckStatus.Fail, "Age", $"Under {MinimumAge}: cannot enter a credit agreement.");
        else if (x.AgeYears >= ReviewFromAge)
            Add(CheckStatus.Warn, "Age", $"{x.AgeYears} years old: check that credit life cover is available.");
        else
            Add(CheckStatus.Pass, "Age", $"{x.AgeYears} years old.");

        Add(x.LifeVerification switch
            {
                VerificationResult.Confirmed => CheckStatus.Pass,
                VerificationResult.Failed => CheckStatus.Fail,
                VerificationResult.Inconclusive => CheckStatus.Warn,
                _ => CheckStatus.Missing
            }, "Home Affairs",
            x.LifeVerification switch
            {
                VerificationResult.Confirmed => x.LifeVerificationSimulated
                    ? "Simulated pass: no Home Affairs provider is connected yet, so nothing was really checked."
                    : "Confirmed alive on the population register.",
                VerificationResult.Failed => "Home Affairs records this ID number as deceased.",
                VerificationResult.Inconclusive => "Home Affairs could not be checked. Try again.",
                _ => "Not checked yet."
            });

        // ---- consent and contact
        Add(x.PopiaConsent ? CheckStatus.Pass : CheckStatus.Missing, "POPIA consent",
            x.PopiaConsent ? "Recorded." : "Record the client's consent on Other details.");
        Add(x.HasMobileNumber ? CheckStatus.Pass : CheckStatus.Missing, "Mobile number",
            x.HasMobileNumber ? "Captured." : "Needed for DebiCheck approval and the quote PIN.");

        // ---- employment
        var type = x.EmploymentType?.Trim();
        if (string.IsNullOrEmpty(type))
            Add(CheckStatus.Missing, "Employment", "Employment not captured.");
        else if (type.Equals("Unemployed", StringComparison.OrdinalIgnoreCase))
            Add(CheckStatus.Fail, "Employment", "Unemployed: no regular income to repay from.");
        else if (type is "Contract" or "Temporary" or "Self-employed")
            Add(CheckStatus.Warn, "Employment", $"{type}: income is less certain. Check the payslips or bank statements.");
        else
            Add(CheckStatus.Pass, "Employment", $"{type}.");

        if (x.EmployedSince is { } since && !string.Equals(type, "Pensioner", StringComparison.OrdinalIgnoreCase))
        {
            var months = MonthsBetween(since, x.Today);
            if (months < MinimumMonthsEmployed)
                Add(CheckStatus.Warn, "Time in job", $"{Math.Max(months, 0)} month(s): may still be on probation.");
            else
                Add(CheckStatus.Pass, "Time in job", months >= 24 ? $"{months / 12} years." : $"{months} months.");
        }

        if (x.ContractExpiryDate is { } expiry)
        {
            if (expiry < x.Today)
                Add(CheckStatus.Fail, "Contract", $"The employment contract ended on {expiry:d MMM yyyy}.");
            else if (expiry < x.Today.AddMonths(ContractEndingWithinMonths))
                Add(CheckStatus.Warn, "Contract", $"The employment contract ends on {expiry:d MMM yyyy}: keep the term inside it.");
        }

        Add(x.EmploymentVerification switch
            {
                VerificationResult.Confirmed => CheckStatus.Pass,
                VerificationResult.Failed => CheckStatus.Fail,
                VerificationResult.Inconclusive => CheckStatus.Warn,
                _ => CheckStatus.Missing
            }, "Employment verified",
            x.EmploymentVerification switch
            {
                VerificationResult.Confirmed => "The employer confirmed it.",
                VerificationResult.Failed => "The employer did not confirm the client works there.",
                VerificationResult.Inconclusive => "Could not reach the employer yet.",
                _ => "Not verified yet. Phone the employer or check the payslip."
            });

        // ---- affordability
        if (x.NetMonthlyIncome is not > 0)
            Add(CheckStatus.Missing, "Income", "Net income not captured.");
        else
            Add(CheckStatus.Pass, "Income", $"Net R {x.NetMonthlyIncome:N2} a month.");

        if (!x.BudgetCaptured)
            Add(CheckStatus.Missing, "Budget", "Capture the monthly budget on the Financial step.");
        else if (x.NetOfNet is { } left)
        {
            if (left <= 0m)
                Add(CheckStatus.Fail, "Affordability", $"Nothing left after expenses and debt (R {left:N2}).");
            else if (left < ThinNetOfNet)
                Add(CheckStatus.Warn, "Affordability", $"Only R {left:N2} left a month after expenses and debt.");
            else
                Add(CheckStatus.Pass, "Affordability", $"R {left:N2} left a month after expenses and debt.");
        }

        if (x.StatementFlag is { } flag)
            Add(flag switch
                {
                    StatementFlag.Good => CheckStatus.Pass,
                    StatementFlag.Warning => CheckStatus.Warn,
                    _ => CheckStatus.Fail
                }, "Bank statement",
                flag switch
                {
                    StatementFlag.Good => "The statement assessment found nothing of concern.",
                    StatementFlag.Warning => "The statement assessment raised warnings. Read it.",
                    _ => "The statement assessment flagged it Bad. Read it before going further."
                });

        // ---- bank account
        if (!x.HasBankAccount)
            Add(CheckStatus.Missing, "Bank account", "No bank account: DebiCheck needs one.");
        else if (!x.BankAccountVerified)
            Add(CheckStatus.Warn, "Bank account", "Captured but not verified (AVS).");
        else
            Add(CheckStatus.Pass, "Bank account", "Verified (AVS).");

        // ---- history with this lender
        var firstTime = x.PreviousLoans == 0;
        if (x.WrittenOffLoans > 0)
            Add(CheckStatus.Fail, "History", $"{x.WrittenOffLoans} loan(s) written off with you before.");
        else if (x.DaysInArrears > ArrearsRefusalDays)
            Add(CheckStatus.Fail, "History", $"{x.DaysInArrears} days in arrears on an existing loan.");
        else if (x.DaysInArrears > 0)
            Add(CheckStatus.Warn, "History", $"{x.DaysInArrears} day(s) behind on an existing loan.");
        else if (x.ActiveLoans > 0)
            Add(CheckStatus.Warn, "History", $"Already has {x.ActiveLoans} loan(s) running with you, up to date.");
        else if (firstTime)
            Add(CheckStatus.Pass, "History", "First time applying with you.");
        else
            Add(CheckStatus.Pass, "History", $"{x.PreviousLoans} earlier loan(s), all repaid.");

        var outcome =
            checks.Any(c => c.Status == CheckStatus.Fail) ? ScreeningOutcome.NotGoodToGo
            : checks.Any(c => c.Status is CheckStatus.Warn or CheckStatus.Missing) ? ScreeningOutcome.Review
            : ScreeningOutcome.GoodToGo;

        var score = 100 - checks.Sum(c => c.Status switch
        {
            CheckStatus.Fail => 40,
            CheckStatus.Missing => 12,
            CheckStatus.Warn => 8,
            _ => 0
        });

        return new GoodToGoResult(outcome, Math.Clamp(score, 0, 100), firstTime, checks);
    }

    public static string Label(ScreeningOutcome outcome) => outcome switch
    {
        ScreeningOutcome.GoodToGo => "Good to go",
        ScreeningOutcome.Review => "Review",
        _ => "Not good to go"
    };

    public static int AgeOn(DateTime dateOfBirth, DateTime today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth.Date > today.AddYears(-age)) age--;
        return age;
    }

    private static int MonthsBetween(DateTime from, DateTime to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        return months;
    }
}
