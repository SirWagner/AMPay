using AMPay.Domain.Credit;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Credit;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

public class AffordabilityTests
{
    private static AffordabilityService Service(AffordabilityNorms? norms = null) =>
        new(Options.Create(norms ?? new AffordabilityNorms()));

    // ----------------------------------------------------------------- expense norms

    [Theory]
    [InlineData(500, 500)]           // the whole of a very low income is minimum expense
    [InlineData(800, 800)]
    [InlineData(3_000, 948.50)]      // 800 + 6.75% of 2 200
    [InlineData(6_250, 1_167.88)]    // top of the band, matching the next band's base
    [InlineData(10_000, 1_505.38)]   // 1 167.88 + 9% of 3 750
    [InlineData(25_000, 2_855.38)]
    [InlineData(40_000, 4_092.88)]   // 2 855.38 + 8.25% of 15 000
    [InlineData(60_000, 5_593.51)]   // 4 918.51 + 6.75% of 10 000
    public void MinimumExpenses_FollowTheRegulation23ABands(decimal income, decimal expected) =>
        Assert.Equal(expected, new AffordabilityNorms().MinimumExpensesFor(income));

    [Fact]
    public void MinimumExpenses_AreZeroWithoutIncome() =>
        Assert.Equal(0m, new AffordabilityNorms().MinimumExpensesFor(0m));

    // ----------------------------------------------------------------- data gaps

    [Fact]
    public void NoIncomeOnFile_IsInsufficient_NotADecline()
    {
        // The distinction matters. A decline is a credit decision about the client;
        // this is a gap in our own capture.
        var result = Service().Assess(new AffordabilityInput(0m, 0m, 0m, 0m, 1_500m));

        Assert.Equal(AffordabilityOutcome.Insufficient, result.Outcome);
        Assert.Contains("No income on file", result.Reasoning);
    }

    // ----------------------------------------------------------------- the expense floor

    [Fact]
    public void AnUnderDeclaredExpense_IsReplacedByTheStatutoryMinimum()
    {
        // Client claims R200 a month of living costs on R10 000 gross. The norm is R1 505.38.
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 10_000m,
            NetMonthlyIncome: 8_500m,
            DeclaredMonthlyExpenses: 200m,
            ExistingDebtRepayments: 0m,
            ProposedInstalment: 1_000m));

        Assert.Equal(1_505.38m, result.StatutoryMinimumExpenses);
        Assert.Equal(1_505.38m, result.AppliedExpenses);
        Assert.Equal(6_994.62m, result.DiscretionaryIncome);
        Assert.Contains("Regulation 23A minimum", result.Reasoning);

        // Same shape as every other amount in the app, not the en-ZA "R6 994,62".
        Assert.Contains("R 6,994.62", result.Reasoning);
    }

    [Fact]
    public void ADeclaredExpenseAboveTheMinimum_IsUsedAsDeclared()
    {
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 10_000m,
            NetMonthlyIncome: 8_500m,
            DeclaredMonthlyExpenses: 4_000m,
            ExistingDebtRepayments: 0m,
            ProposedInstalment: 1_000m));

        Assert.Equal(4_000m, result.AppliedExpenses);
        Assert.Equal(4_500m, result.DiscretionaryIncome);
    }

    [Fact]
    public void ExistingDebtIsDeductedFromDiscretionaryIncome()
    {
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 10_000m,
            NetMonthlyIncome: 8_500m,
            DeclaredMonthlyExpenses: 4_000m,
            ExistingDebtRepayments: 1_500m,
            ProposedInstalment: 1_000m));

        Assert.Equal(3_000m, result.DiscretionaryIncome);
    }

    // ----------------------------------------------------------------- other income

    [Fact]
    public void OtherIncome_CountsTowardsNetIncome_AndTheIncomeBand()
    {
        // R10 000 salary plus R2 000 other income is a R12 000 household for Regulation
        // 23A: a norm of 1 167.88 + 9% of 5 750 = R1 685.38, not the R1 505.38 of salary alone.
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 10_000m,
            NetMonthlyIncome: 8_500m,
            DeclaredMonthlyExpenses: 200m,
            ExistingDebtRepayments: 0m,
            ProposedInstalment: 1_000m,
            OtherMonthlyIncome: 2_000m));

        Assert.Equal(1_685.38m, result.StatutoryMinimumExpenses);
        Assert.Equal(8_814.62m, result.DiscretionaryIncome);
        Assert.Contains("plus other income R 2,000.00", result.Reasoning);
    }

    [Fact]
    public void OtherIncomeAlone_IsStillInsufficient()
    {
        // This lender advances against a payslip. Declared other income with no salary on
        // file is a capture gap, not something to lend against.
        var result = Service().Assess(new AffordabilityInput(
            0m, 0m, 0m, 0m, 500m, OtherMonthlyIncome: 6_000m));

        Assert.Equal(AffordabilityOutcome.Insufficient, result.Outcome);
    }

    // ----------------------------------------------------------------- outcomes

    [Fact]
    public void AComfortableInstalment_Passes()
    {
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 25_000m,
            NetMonthlyIncome: 20_000m,
            DeclaredMonthlyExpenses: 8_000m,
            ExistingDebtRepayments: 2_000m,
            ProposedInstalment: 2_000m));

        Assert.Equal(AffordabilityOutcome.Pass, result.Outcome);
        Assert.Equal(10_000m, result.DiscretionaryIncome);
        Assert.Equal(8_000m, result.SurplusAfterInstalment);
        Assert.Equal(0.20m, result.UtilisationRatio);
    }

    [Fact]
    public void AnInstalmentEatingMostOfTheSurplus_IsMarginal()
    {
        // 80% utilisation: affordable on paper, but above the 75% comfort threshold.
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 25_000m,
            NetMonthlyIncome: 20_000m,
            DeclaredMonthlyExpenses: 8_000m,
            ExistingDebtRepayments: 2_000m,
            ProposedInstalment: 8_000m));

        Assert.Equal(AffordabilityOutcome.Marginal, result.Outcome);
        Assert.Contains("needs a human decision", result.Reasoning);
    }

    [Fact]
    public void AnInstalmentBeyondDiscretionaryIncome_Fails()
    {
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 25_000m,
            NetMonthlyIncome: 20_000m,
            DeclaredMonthlyExpenses: 8_000m,
            ExistingDebtRepayments: 2_000m,
            ProposedInstalment: 12_000m));

        Assert.Equal(AffordabilityOutcome.Fail, result.Outcome);
        Assert.Equal(10_000m, result.MaximumAffordableInstalment);
        Assert.Contains("cannot service this loan", result.Reasoning);
    }

    [Fact]
    public void NoDiscretionaryIncomeAtAll_Fails()
    {
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 10_000m,
            NetMonthlyIncome: 8_000m,
            DeclaredMonthlyExpenses: 6_000m,
            ExistingDebtRepayments: 2_500m,
            ProposedInstalment: 500m));

        Assert.Equal(AffordabilityOutcome.Fail, result.Outcome);
        Assert.Equal(0m, result.MaximumAffordableInstalment);
        Assert.Contains("reckless", result.Reasoning);
    }

    [Fact]
    public void AnInstalmentExactlyAtDiscretionaryIncome_IsMarginalRatherThanFailed()
    {
        // 100% utilisation is affordable by the letter of the test, but no lender should
        // write it without someone looking at it.
        var result = Service().Assess(new AffordabilityInput(
            GrossMonthlyIncome: 25_000m,
            NetMonthlyIncome: 20_000m,
            DeclaredMonthlyExpenses: 8_000m,
            ExistingDebtRepayments: 2_000m,
            ProposedInstalment: 10_000m));

        Assert.Equal(AffordabilityOutcome.Marginal, result.Outcome);
        Assert.Equal(0m, result.SurplusAfterInstalment);
        Assert.Equal(1m, result.UtilisationRatio);
    }

    [Fact]
    public void TheComfortThresholdIsConfigurable()
    {
        var strict = new AffordabilityNorms { MarginalUtilisationThreshold = 0.25m };

        var result = Service(strict).Assess(new AffordabilityInput(
            GrossMonthlyIncome: 25_000m,
            NetMonthlyIncome: 20_000m,
            DeclaredMonthlyExpenses: 8_000m,
            ExistingDebtRepayments: 2_000m,
            ProposedInstalment: 3_000m));

        Assert.Equal(AffordabilityOutcome.Marginal, result.Outcome);
    }
}
