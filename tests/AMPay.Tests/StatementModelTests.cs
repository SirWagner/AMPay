using AMPay.Domain.Credit;
using AMPay.Domain.Entities;

namespace AMPay.Tests;

/// <summary>The bank-statement rules model: declared figures validated against the statement.</summary>
public class StatementModelTests
{
    /// <summary>Three months, paid every month, nothing wrong.</summary>
    private static StatementFigures Clean() => new()
    {
        MonthsCovered = 3,
        SalaryDeposits = 3,
        AverageSalaryDeposit = 15_000m,
        OtherLenders = 1,
        MonthlyLoanRepayments = 1_000m
    };

    private static GoodToGoCheck Finding(StatementVerdict v, string name) => v.Findings.Single(f => f.Name == name);

    [Fact]
    public void AStatementThatAgreesWithWhatWasDeclared_IsGood()
    {
        var v = StatementModel.Evaluate(Clean(), declaredNetIncome: 15_000m, declaredDebtInstalments: 1_000m);

        Assert.Equal(StatementFlag.Good, v.Flag);
        Assert.All(v.Findings, f => Assert.Equal(CheckStatus.Pass, f.Status));
    }

    [Theory]
    [InlineData(15_050, CheckStatus.Pass)]   // R50 over: rounding
    [InlineData(16_000, CheckStatus.Pass)]   // about 6% over
    [InlineData(17_500, CheckStatus.Warn)]   // about 14% over
    [InlineData(21_000, CheckStatus.Fail)]   // about 29% over
    public void DeclaredIncome_IsValidatedAgainstWhatLandsInTheAccount(decimal declared, CheckStatus expected)
    {
        var v = StatementModel.Evaluate(Clean(), declared, 1_000m);

        Assert.Equal(expected, Finding(v, "Declared income").Status);
    }

    [Fact]
    public void IncomeHigherThanDeclared_IsNotAProblem()
    {
        var v = StatementModel.Evaluate(Clean() with { AverageSalaryDeposit = 20_000m }, 15_000m, 1_000m);

        Assert.Equal(StatementFlag.Good, v.Flag);
    }

    [Fact]
    public void NoSalaryOnTheStatement_IsBad_AndIrregularSalaryIsAWarning()
    {
        var none = StatementModel.Evaluate(Clean() with { SalaryDeposits = 0, AverageSalaryDeposit = 0m }, 15_000m, 1_000m);
        var twoOfThree = StatementModel.Evaluate(Clean() with { SalaryDeposits = 2 }, 15_000m, 1_000m);

        Assert.Equal(StatementFlag.Bad, none.Flag);
        Assert.DoesNotContain(none.Findings, f => f.Name == "Declared income");
        Assert.Equal(CheckStatus.Warn, Finding(twoOfThree, "Salary").Status);
        Assert.Equal(StatementFlag.Warning, twoOfThree.Flag);
    }

    [Theory]
    [InlineData(1_000, 1_050, CheckStatus.Pass)]   // within R100
    [InlineData(1_000, 1_300, CheckStatus.Warn)]   // 30% more than declared
    [InlineData(1_000, 2_000, CheckStatus.Fail)]   // double what was declared
    [InlineData(0, 800, CheckStatus.Fail)]         // nothing declared at all
    public void DebtOnTheStatement_IsComparedWithWhatWasDeclared(decimal declared, decimal onStatement, CheckStatus expected)
    {
        var v = StatementModel.Evaluate(Clean() with { MonthlyLoanRepayments = onStatement }, 15_000m, declared);

        Assert.Equal(expected, Finding(v, "Other lenders").Status);
    }

    [Fact]
    public void ManyLenders_AllDeclared_IsStillAWarning()
    {
        var v = StatementModel.Evaluate(Clean() with { OtherLenders = 3, MonthlyLoanRepayments = 2_400m }, 15_000m, 2_400m);

        Assert.Equal(CheckStatus.Warn, Finding(v, "Other lenders").Status);
    }

    [Theory]
    [InlineData(0, CheckStatus.Pass)]
    [InlineData(1, CheckStatus.Warn)]
    [InlineData(3, CheckStatus.Fail)]
    public void ReturnedDebitOrders_Escalate(int returned, CheckStatus expected)
    {
        var v = StatementModel.Evaluate(Clean() with { ReturnedDebitOrders = returned }, 15_000m, 1_000m);

        Assert.Equal(expected, Finding(v, "Returned debit orders").Status);
    }

    [Theory]
    [InlineData(200, CheckStatus.Pass)]     // just over 1% of income
    [InlineData(600, CheckStatus.Warn)]     // 4%
    [InlineData(2_000, CheckStatus.Fail)]   // 13%
    public void Gambling_IsJudgedAgainstIncome(decimal monthly, CheckStatus expected)
    {
        var v = StatementModel.Evaluate(Clean() with { MonthlyGambling = monthly }, 15_000m, 1_000m);

        Assert.Equal(expected, Finding(v, "Gambling").Status);
    }

    [Theory]
    [InlineData(0, CheckStatus.Pass)]
    [InlineData(4, CheckStatus.Warn)]
    [InlineData(15, CheckStatus.Fail)]   // five days a month over three months
    public void DaysBelowZero_AreJudgedPerMonth(int days, CheckStatus expected)
    {
        var v = StatementModel.Evaluate(Clean() with { DaysOverdrawn = days }, 15_000m, 1_000m);

        Assert.Equal(expected, Finding(v, "Balance").Status);
    }

    [Fact]
    public void AShortStatement_OrNoDeclaredIncome_IsAWarning_NotARefusal()
    {
        var oneMonth = StatementModel.Evaluate(Clean() with { MonthsCovered = 1, SalaryDeposits = 1 }, 15_000m, 1_000m);
        var noIncome = StatementModel.Evaluate(Clean(), declaredNetIncome: null, 1_000m);

        Assert.Equal(StatementFlag.Warning, oneMonth.Flag);
        Assert.Equal(StatementFlag.Warning, noIncome.Flag);
        Assert.Equal(CheckStatus.Missing, Finding(noIncome, "Declared income").Status);
    }

    [Fact]
    public void TheWorstFindingSetsTheFlag_AndTheSummaryNamesIt()
    {
        var v = StatementModel.Evaluate(Clean() with { ReturnedDebitOrders = 4, DaysOverdrawn = 2 }, 15_000m, 1_000m);

        Assert.Equal(StatementFlag.Bad, v.Flag);
        Assert.Contains("returned debit orders", v.Summary);
        Assert.DoesNotContain("balance", v.Summary); // a warning, so not a reason the loan is unsupported
        Assert.Equal(v.Findings, GoodToGoResult.ParseChecks(v.SerialisedFindings));
    }
}
