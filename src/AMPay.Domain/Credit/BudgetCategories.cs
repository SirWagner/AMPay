using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Credit;

/// <summary>
/// The standard budget lines on the Financial step, in the order Maxmoney lists them.
/// <para>
/// Maxmoney marks every line "Expense". Three of them - Credit Card, Loans and Vehicle
/// Installment - are instalments to credit providers, and they are classed as debt here.
/// The difference matters: the Regulation 23A minimum is a floor on living costs, so a
/// client who declares R2 000 of groceries and R3 000 of loan instalments has declared
/// R2 000 of living expenses, not R5 000. Counting the loans as expenses would let them
/// hide under the statutory floor and be deducted once instead of twice.
/// </para>
/// <para>
/// Income is not here. Own Salary and Other Income are the income fields at the top of
/// the same step; a second copy of them as budget lines could only disagree.
/// </para>
/// </summary>
public static class BudgetCategories
{
    /// <summary>The category of a line the operator added with "Add expense".</summary>
    public const string Custom = "Custom";

    public record Category(string Key, string Label, BudgetLineKind Kind, string? Hint = null);

    public static readonly IReadOnlyList<Category> Standard = new Category[]
    {
        new("Groceries",         "Groceries",           BudgetLineKind.Expense),
        new("Travel",            "Travel",              BudgetLineKind.Expense),
        new("MortgageRent",      "Mortgage / Rent",     BudgetLineKind.Expense),
        new("Education",         "Education",           BudgetLineKind.Expense),
        new("Phone",             "Phone",               BudgetLineKind.Expense),
        new("Alimony",           "Alimony",             BudgetLineKind.Expense),
        new("CreditCard",        "Credit card",         BudgetLineKind.DebtInstalment),
        new("Loans",             "Loans",               BudgetLineKind.DebtInstalment,
            "Instalments to other lenders."),
        new("Clothing",          "Clothing",            BudgetLineKind.Expense),
        new("OtherMedical",      "Other medical",       BudgetLineKind.Expense),
        new("Other",             "Other",               BudgetLineKind.Expense),
        new("VehicleInstalment", "Vehicle instalment",  BudgetLineKind.DebtInstalment),
        new("GasOil",            "Gas & oil",           BudgetLineKind.Expense),
        new("WaterElectricity",  "Water & electricity", BudgetLineKind.Expense),
        new("Insurance",         "Insurance",           BudgetLineKind.Expense),
        new("HealthInsurance",   "Health insurance",    BudgetLineKind.Expense),
        new("Taxes",             "Taxes",               BudgetLineKind.Expense,
            "Only tax not already deducted from net pay.")
    };

    /// <summary>
    /// The lines always on screen. Every other category is offered from a list and shown
    /// once chosen or once it holds an amount - seventeen rows, mostly zero, buried the
    /// three that nearly every client has.
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
}
