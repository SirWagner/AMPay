using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Tests;

public class BudgetCategoriesTests
{
    [Theory]
    [InlineData("CreditCard")]
    [InlineData("Loans")]
    [InlineData("VehicleInstalment")]
    public void InstalmentsToCreditProviders_AreDebt_NotLivingExpenses(string key) =>
        // Maxmoney lists these as expenses. Counted that way they would hide beneath the
        // Regulation 23A floor instead of being deducted on top of it.
        Assert.Equal(BudgetLineKind.DebtInstalment, BudgetCategories.Find(key)!.Kind);

    [Theory]
    [InlineData("Groceries")]
    [InlineData("MortgageRent")]
    [InlineData("Taxes")]
    public void EverydayCosts_AreExpenses(string key) =>
        Assert.Equal(BudgetLineKind.Expense, BudgetCategories.Find(key)!.Kind);

    [Fact]
    public void CategoryKeys_AreUnique_AndFitTheColumn()
    {
        var keys = BudgetCategories.Standard.Select(c => c.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(k.Length <= 40));
        Assert.DoesNotContain(BudgetCategories.Custom, keys);
    }

    [Fact]
    public void UnknownCategory_IsNotFound()
    {
        Assert.Null(BudgetCategories.Find("Nonsense"));
        Assert.Null(BudgetCategories.Find(null));
    }

    [Fact]
    public void Totals_SplitExpensesFromDebt()
    {
        // The budget in the Maxmoney screenshot, plus a R1 500 loan instalment.
        var lines = new[]
        {
            Line(BudgetLineKind.Expense, 2_000m),        // Groceries
            Line(BudgetLineKind.Expense, 1_500m),        // Travel
            Line(BudgetLineKind.Expense, 1_200m),        // Mortgage / Rent
            Line(BudgetLineKind.Expense, 1_000m),        // Education
            Line(BudgetLineKind.Expense, 200m),          // Phone
            Line(BudgetLineKind.DebtInstalment, 1_500m)  // Loans
        };

        Assert.Equal(5_900m, BudgetCategories.TotalExpenses(lines));
        Assert.Equal(1_500m, BudgetCategories.TotalDebtInstalments(lines));
    }

    private static ClientBudget Line(BudgetLineKind kind, decimal amount) =>
        new() { Kind = kind, Amount = amount, Description = "x" };
}

public class BudgetLineVisibilityTests
{
    [Theory]
    [InlineData("Groceries")]
    [InlineData("Travel")]
    [InlineData("MortgageRent")]
    public void TheMainLines_AreAlwaysShown(string key) =>
        Assert.True(new AMPay.Web.Models.BudgetLineModel { Category = key }.IsVisible);

    [Fact]
    public void AnEmptyOptionalLine_WaitsInTheList() =>
        Assert.False(new AMPay.Web.Models.BudgetLineModel { Category = "Loans" }.IsVisible);

    [Fact]
    public void AnOptionalLineWithAnAmount_IsNeverHidden() =>
        // A saved loan instalment disappearing from view would be easy to miss - and it is
        // still being deducted.
        Assert.True(new AMPay.Web.Models.BudgetLineModel { Category = "Loans", Amount = 1_500m }.IsVisible);

    [Fact]
    public void AnOptionalLineChosenFromTheList_StaysWhileEmpty() =>
        Assert.True(new AMPay.Web.Models.BudgetLineModel { Category = "Insurance", Shown = true }.IsVisible);

    [Fact]
    public void ACustomLine_IsShown() =>
        Assert.True(new AMPay.Web.Models.BudgetLineModel { Category = BudgetCategories.Custom }.IsVisible);

    [Fact]
    public void ExactlyThreeLinesAreMain() =>
        Assert.Equal(3, BudgetCategories.Standard.Count(c => BudgetCategories.IsMain(c.Key)));
}
