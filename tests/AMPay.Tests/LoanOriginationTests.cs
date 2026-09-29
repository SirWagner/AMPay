using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Credit;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

public class LoanOriginationTests
{
    private static readonly DateTime FirstCollection = new(2026, 10, 25);

    private static LoanQuote Quote(decimal principal = 10_000m, int term = 6) =>
        new LoanPricingService(Options.Create(new NcaCreditLimits())).Quote(
            new LoanQuoteRequest(principal, term, FirstCollection),
            new CreditPackage
            {
                Tier = CreditTier.Regular,
                Name = "Regular",
                MonthlyInterestRate = 0.05m,
                MonthlyServiceFee = 16m,
                InitiationFeeRate = 0.15m,
                CreditLifeRate = 0.0045m,
                MinLoanAmount = 500m,
                MaxLoanAmount = 100_000m,
                MinTermMonths = 1,
                MaxTermMonths = 60
            });

    // ----------------------------------------------------------------- quote -> loan

    [Fact]
    public void ApplyQuote_CopiesEveryFigureTheAgreementIsMadeOf()
    {
        var quote = Quote();
        var loan = new Loan();

        LoanOrigination.ApplyQuote(loan, quote);

        Assert.Equal(quote.Principal, loan.Principal);
        Assert.Equal(quote.NumberOfInstalments, loan.NumberOfInstalments);
        Assert.Equal(quote.InitiationFee, loan.InitiationFee);
        Assert.Equal(quote.CapitalisedAmount, loan.CapitalisedAmount);
        Assert.Equal(quote.FirstInstalment, loan.FirstInstalment);
        Assert.Equal(quote.FinalInstalment, loan.FinalInstalment);
        Assert.Equal(quote.TotalInterest, loan.TotalInterest);
        Assert.Equal(quote.TotalServiceFees, loan.TotalServiceFees);
        Assert.Equal(quote.TotalCreditLife, loan.TotalCreditLife);
        Assert.Equal(quote.TotalRepayable, loan.TotalRepayable);
        Assert.Equal(quote.TotalCostOfCredit, loan.TotalCostOfCredit);
        Assert.Equal(FirstCollection, loan.FirstCollectionDate);
    }

    [Fact]
    public void ApplyQuote_StoresTheRatesActuallyUsed_NotThePackageListPrice()
    {
        // A package priced above the ceiling is clamped. The loan must record what was
        // charged, or the agreement on file would overstate the client's cost.
        var quote = new LoanPricingService(Options.Create(new NcaCreditLimits())).Quote(
            new LoanQuoteRequest(10_000m, 6, FirstCollection),
            new CreditPackage
            {
                Name = "Overpriced", MonthlyInterestRate = 0.09m, MonthlyServiceFee = 120m,
                InitiationFeeRate = 0.15m, CreditLifeRate = 0.01m,
                MinLoanAmount = 1m, MaxLoanAmount = 100_000m, MinTermMonths = 1, MaxTermMonths = 60
            });

        var loan = new Loan();
        LoanOrigination.ApplyQuote(loan, quote);

        Assert.Equal(0.05m, loan.MonthlyInterestRate);
        Assert.Equal(69m, loan.MonthlyServiceFee);
        Assert.Equal(0.0045m, loan.CreditLifeRate);
    }

    [Fact]
    public void ApplyQuote_ReturnsTheScheduleKeyedToTheLoan_WithoutAttachingIt()
    {
        var quote = Quote();
        var loan = new Loan();

        var rows = LoanOrigination.ApplyQuote(loan, quote);

        Assert.Equal(quote.Schedule.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(loan.Id, r.LoanId));
        Assert.Equal(quote.Schedule.Select(s => s.InstalmentTotal), rows.Select(r => r.InstalmentTotal));

        // Attaching through the navigation of a tracked parent makes EF issue an UPDATE for a
        // row that does not exist; the caller adds these through the DbSet instead.
        Assert.Empty(loan.Schedule);
    }

    // ----------------------------------------------------------------- affordability

    [Fact]
    public void AffordabilityInput_IsNullWithoutAFinancialRecord() =>
        Assert.Null(LoanOrigination.AffordabilityInputFor(null, 1_000m));

    [Fact]
    public void AffordabilityInput_UsesNetPay_AndIgnoresUnevidencedOtherIncome()
    {
        var input = LoanOrigination.AffordabilityInputFor(new ClientFinancial
        {
            GrossMonthlyIncome = 20_000m,
            NetMonthlyIncome = 16_000m,
            OtherIncome = 5_000m,
            TotalMonthlyExpenses = 6_000m,
            TotalMonthlyDebtRepayments = 1_500m
        }, 1_200m);

        Assert.NotNull(input);
        Assert.Equal(16_000m, input!.NetMonthlyIncome);
        Assert.Equal(6_000m, input.DeclaredMonthlyExpenses);
        Assert.Equal(1_500m, input.ExistingDebtRepayments);
        Assert.Equal(1_200m, input.ProposedInstalment);
    }

    [Fact]
    public void AffordabilityInput_AddsWhatTheClientAlreadyOwesUs_ToDeclaredDebt()
    {
        // Declared debt was captured at onboarding, before any of our loans existed, so it
        // cannot include them. Without this a second loan is assessed as if the first were
        // not being repaid.
        var input = LoanOrigination.AffordabilityInputFor(new ClientFinancial
        {
            GrossMonthlyIncome = 18_000m,
            NetMonthlyIncome = 14_500m,
            TotalMonthlyExpenses = 5_200m,
            TotalMonthlyDebtRepayments = 1_800m
        }, proposedInstalment: 800m, ownInstalments: 1_142.43m);

        Assert.Equal(2_942.43m, input!.ExistingDebtRepayments);
    }

    [Fact]
    public void ASecondLoan_ThatPassesAlone_FailsOnceTheFirstIsCounted()
    {
        var financial = new ClientFinancial
        {
            GrossMonthlyIncome = 18_000m,
            NetMonthlyIncome = 14_500m,
            TotalMonthlyExpenses = 5_200m,
            TotalMonthlyDebtRepayments = 1_800m
        };
        var service = new AffordabilityService(Options.Create(new AffordabilityNorms()));

        // Discretionary income is R7 500. A R7 000 instalment fits on its own...
        var alone = service.Assess(LoanOrigination.AffordabilityInputFor(financial, 7_000m)!);
        Assert.NotEqual(AffordabilityOutcome.Fail, alone.Outcome);

        // ...but not on top of the R1 142.43 the client already repays us.
        var withFirst = service.Assess(LoanOrigination.AffordabilityInputFor(financial, 7_000m, 1_142.43m)!);
        Assert.Equal(AffordabilityOutcome.Fail, withFirst.Outcome);
    }

    [Fact]
    public void ToAssessment_SnapshotsInputsAndOutcome()
    {
        var input = new AffordabilityInput(20_000m, 16_000m, 6_000m, 1_500m, 1_200m);
        var result = new AffordabilityService(Options.Create(new AffordabilityNorms())).Assess(input);
        var loanId = Guid.NewGuid();

        var a = LoanOrigination.ToAssessment(loanId, input, result, "user-1");

        Assert.Equal(loanId, a.LoanId);
        Assert.Equal("user-1", a.AssessedByUserId);
        Assert.Equal(16_000m, a.NetMonthlyIncome);
        Assert.Equal(result.Outcome, a.Outcome);
        Assert.Equal(result.DiscretionaryIncome, a.DiscretionaryIncome);
        Assert.Equal(result.Reasoning, a.Reasoning);
        Assert.False(a.WasOverridden);
    }

    // ----------------------------------------------------------------- loan numbers

    [Theory]
    [InlineData(null, "LN-000001")]
    [InlineData("", "LN-000001")]
    [InlineData("LN-000001", "LN-000002")]
    [InlineData("LN-000099", "LN-000100")]
    [InlineData("LN-999998", "LN-999999")]
    [InlineData("garbage", "LN-000001")]
    public void NextLoanNumber_Increments(string? highest, string expected) =>
        Assert.Equal(expected, LoanOrigination.NextLoanNumber(highest));

    // ----------------------------------------------------------------- collection dates

    [Fact]
    public void FirstCollection_IsThisMonthsPayDay_WhenFarEnoughAway() =>
        // 1 Oct, paid on the 25th: 24 days out, well past the week's lead time.
        Assert.Equal(new DateTime(2026, 10, 25),
            LoanOrigination.DefaultFirstCollectionDate(25, new DateTime(2026, 10, 1)));

    [Fact]
    public void FirstCollection_RollsToNextMonth_WhenPayDayIsTooClose() =>
        // 22 Oct, paid on the 25th: only 3 days out, not enough to authenticate a mandate.
        Assert.Equal(new DateTime(2026, 11, 25),
            LoanOrigination.DefaultFirstCollectionDate(25, new DateTime(2026, 10, 22)));

    [Fact]
    public void FirstCollection_PayDayOfThe31st_LandsOnTheLastDayOfAShortMonth() =>
        Assert.Equal(new DateTime(2027, 2, 28),
            LoanOrigination.DefaultFirstCollectionDate(31, new DateTime(2027, 1, 28)));

    [Fact]
    public void FirstCollection_WithoutAPayDay_IsTheLastDayOfNextMonth() =>
        Assert.Equal(new DateTime(2026, 11, 30),
            LoanOrigination.DefaultFirstCollectionDate(null, new DateTime(2026, 10, 9)));

    [Theory]
    [InlineData(2026, 10, 25, "25")]
    [InlineData(2026, 10, 5, "05")]
    [InlineData(2026, 10, 31, "LDOM")]
    [InlineData(2027, 2, 28, "LDOM")]
    public void CollectionDay_UsesLdomForTheLastDayOfTheMonth(int y, int m, int d, string expected) =>
        Assert.Equal(expected, LoanOrigination.CollectionDayFor(new DateTime(y, m, d)));
}
