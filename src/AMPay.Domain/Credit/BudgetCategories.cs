using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Credit;

/// <summary>
/// The standard budget on the Financial step: every line a lender asks about, grouped the way
/// a client thinks about their month. It holds Maxmoney's sixteen expense lines and adds the
/// payslip deductions and the household costs a South African affordability assessment needs.
/// <para>
/// Every standard line is captured for every client, zero included, so the file shows each was
/// asked about (NCA s81, Regulation 23A). Staff may fold away the empty ones on screen; they are
/// still posted and saved.
/// </para>
/// <para>
/// Three kinds, kept apart because they are counted differently:
/// <list type="bullet">
/// <item><see cref="BudgetLineKind.PayslipDeduction"/> - taken off before the salary is paid.
/// They explain how gross becomes net and are never deducted from net pay a second time.</item>
/// <item><see cref="BudgetLineKind.Expense"/> - living costs, tested as a whole against the
/// Regulation 23A minimum.</item>
/// <item><see cref="BudgetLineKind.DebtInstalment"/> - instalments to credit providers, deducted on
/// top of living costs and never counted towards the Regulation 23A floor.</item>
/// </list>
/// </para>
/// <para>
/// Income is not here: gross, net and other income are the fields at the top of the same step.
/// </para>
/// </summary>
public static class BudgetCategories
{
    /// <summary>The category of a line the operator added with "Add something else".</summary>
    public const string Custom = "Custom";

    public record Category(string Key, string Label, BudgetLineKind Kind, string Group, string? Hint = null);

    // Groups, in the order the form shows them.
    public const string Payslip = "Deducted from the salary";
    public const string Housing = "Housing and utilities";
    public const string Food = "Food and household";
    public const string Transport = "Transport";
    public const string Family = "Family and dependants";
    public const string Health = "Health and insurance";
    public const string Communication = "Phone and subscriptions";
    public const string Other = "Other";
    public const string Debt = "Debt repayments";

    public static readonly IReadOnlyList<string> Groups =
        new[] { Payslip, Housing, Food, Transport, Family, Health, Communication, Other, Debt };

    /// <summary>
    /// Every standard line. Keys are stored on the client's budget rows: never rename one,
    /// only add. The labels may change.
    /// </summary>
    public static readonly IReadOnlyList<Category> Standard = new Category[]
    {
        // ---- off the payslip, before net pay
        new("PayslipTax",        "Income tax (PAYE)",              BudgetLineKind.PayslipDeduction, Payslip),
        new("PayslipUif",        "UIF",                            BudgetLineKind.PayslipDeduction, Payslip),
        new("PayslipPension",    "Pension / provident fund",       BudgetLineKind.PayslipDeduction, Payslip),
        new("PayslipMedical",    "Medical aid (through payroll)",  BudgetLineKind.PayslipDeduction, Payslip),
        new("PayslipUnion",      "Union fees",                     BudgetLineKind.PayslipDeduction, Payslip),
        new("PayslipLoan",       "Loan repaid through payroll",    BudgetLineKind.PayslipDeduction, Payslip,
            "Debt, but already out of net pay: not deducted again."),
        new("PayslipGarnishee",  "Garnishee / emoluments attachment order", BudgetLineKind.PayslipDeduction, Payslip,
            "A court order against the salary. Ask about it."),
        new("PayslipOther",      "Other payslip deductions",       BudgetLineKind.PayslipDeduction, Payslip),

        // ---- living expenses
        new("MortgageRent",      "Mortgage / rent",                BudgetLineKind.Expense, Housing),
        new("RatesLevies",       "Rates, levies and security",     BudgetLineKind.Expense, Housing),
        new("WaterElectricity",  "Water & electricity",            BudgetLineKind.Expense, Housing),
        new("GasOil",            "Gas & paraffin",                 BudgetLineKind.Expense, Housing,
            "Cooking and heating fuel. Fuel for a car goes under Transport."),
        new("DomesticWorker",    "Domestic worker",                BudgetLineKind.Expense, Housing),

        new("Groceries",         "Groceries",                      BudgetLineKind.Expense, Food),
        new("Toiletries",        "Toiletries and cleaning",        BudgetLineKind.Expense, Food),
        new("Clothing",          "Clothing",                       BudgetLineKind.Expense, Food),

        new("Travel",            "Travel (taxi, bus, train)",                BudgetLineKind.Expense, Transport),
        new("VehicleRunning",    "Car fuel and maintenance",       BudgetLineKind.Expense, Transport),

        new("Education",         "School fees and education",      BudgetLineKind.Expense, Family),
        new("Childcare",         "Childcare / crèche",             BudgetLineKind.Expense, Family),
        new("Alimony",           "Maintenance / alimony",          BudgetLineKind.Expense, Family,
            "Court-ordered or agreed maintenance paid by the client."),
        new("FamilySupport",     "Support for family members",     BudgetLineKind.Expense, Family),

        new("HealthInsurance",   "Medical aid (paid privately)",   BudgetLineKind.Expense, Health,
            "Only if not already deducted on the payslip."),
        new("OtherMedical",      "Other medical",                  BudgetLineKind.Expense, Health),
        new("Insurance",         "Insurance (car, household)",     BudgetLineKind.Expense, Health),
        new("FuneralCover",      "Funeral policy / burial society",BudgetLineKind.Expense, Health),

        new("Phone",             "Phone and data",                 BudgetLineKind.Expense, Communication),
        new("Subscriptions",     "TV, internet and subscriptions", BudgetLineKind.Expense, Communication),

        new("Taxes",             "Taxes",                          BudgetLineKind.Expense, Other,
            "Only tax not already deducted from net pay."),
        new("Savings",           "Savings / stokvel",              BudgetLineKind.Expense, Other,
            "Only what the client will keep paying in."),
        new("Other",             "Other",                          BudgetLineKind.Expense, Other),

        // ---- to credit providers
        new("Loans",             "Loans",                          BudgetLineKind.DebtInstalment, Debt,
            "Instalments to other lenders, paid from the bank account."),
        new("CreditCard",        "Credit card",                    BudgetLineKind.DebtInstalment, Debt),
        new("StoreAccounts",     "Store, clothing and furniture accounts", BudgetLineKind.DebtInstalment, Debt),
        new("VehicleInstalment", "Vehicle instalment",             BudgetLineKind.DebtInstalment, Debt)
    };

    /// <summary>
    /// The lines always on screen when the empty ones are folded away: the three nearly every
    /// client has. Every other line is still on the form and still saved.
    /// </summary>
    public static readonly IReadOnlySet<string> Main =
        new HashSet<string> { "Groceries", "Travel", "MortgageRent" };

    public static bool IsMain(string? key) => key is not null && Main.Contains(key);

    public static Category? Find(string? key) =>
        key is null ? null : Standard.FirstOrDefault(c => c.Key == key);

    /// <summary>Sum of the living-expense lines.</summary>
    public static decimal TotalExpenses(IEnumerable<ClientBudget> lines) =>
        lines.Where(l => l.Kind == BudgetLineKind.Expense).Sum(l => l.Amount);

    /// <summary>Sum of the instalments to other credit providers.</summary>
    public static decimal TotalDebtInstalments(IEnumerable<ClientBudget> lines) =>
        lines.Where(l => l.Kind == BudgetLineKind.DebtInstalment).Sum(l => l.Amount);

    /// <summary>Sum of what comes off the payslip. Explains gross to net; never deducted from net.</summary>
    public static decimal TotalPayslipDeductions(IEnumerable<ClientBudget> lines) =>
        lines.Where(l => l.Kind == BudgetLineKind.PayslipDeduction).Sum(l => l.Amount);

    /// <summary>
    /// Gross less the payslip deductions should be about the net pay captured. Returns the gap
    /// (positive: the deductions do not explain all of the difference), or null when it agrees
    /// within <paramref name="tolerance"/> or there is nothing to compare.
    /// </summary>
    public static decimal? PayslipGap(decimal? gross, decimal? net, decimal deductions, decimal tolerance = 100m)
    {
        if (gross is not > 0 || net is not > 0 || deductions <= 0) return null;
        var gap = gross.Value - deductions - net.Value;
        return Math.Abs(gap) <= tolerance ? null : gap;
    }
}
