using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Credit;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

public class LoanPricingTests
{
    private static readonly DateTime FirstCollection = new(2026, 10, 25);

    private static LoanPricingService Service(NcaCreditLimits? limits = null) =>
        new(Options.Create(limits ?? new NcaCreditLimits()));

    private static CreditPackage Package(
        decimal interest = 0.05m,
        decimal serviceFee = 16m,
        decimal initiationRate = 0.15m,
        decimal creditLife = 0.0045m,
        decimal min = 500m,
        decimal max = 100_000m,
        int minTerm = 1,
        int maxTerm = 60) => new()
        {
            Tier = CreditTier.Regular,
            Name = "Regular",
            MonthlyInterestRate = interest,
            MonthlyServiceFee = serviceFee,
            InitiationFeeRate = initiationRate,
            CreditLifeRate = creditLife,
            MinLoanAmount = min,
            MaxLoanAmount = max,
            MinTermMonths = minTerm,
            MaxTermMonths = maxTerm
        };

    // ----------------------------------------------------------------- initiation fee

    [Fact]
    public void InitiationFee_BelowStatutoryMaximum_IsChargedInFull()
    {
        // 15% of R1 000 is R150, under the R189.75 flat cap, so it stands.
        var quote = Service().Quote(new LoanQuoteRequest(1_000m, 1, FirstCollection), Package());

        Assert.Equal(150m, quote.InitiationFee);
        Assert.Empty(quote.Notices);
    }

    [Fact]
    public void InitiationFee_AboveStatutoryMaximum_IsClampedAndReported()
    {
        // 15% of R10 000 is R1 500. The cap is R189.75 + 10% of R9 000 = R1 089.75.
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        Assert.Equal(1_089.75m, quote.InitiationFee);
        Assert.Contains(quote.Notices, n => n.Contains("Initiation fee reduced"));
    }

    [Fact]
    public void Notices_StateAmountsInRands_InTheAppWideFormat()
    {
        // Shown on screen beside figures formatted "R 1,089.75", so they must match -
        // not a bare "1089.75" and not the en-ZA "R1 089,75".
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        var notice = Assert.Single(quote.Notices);
        Assert.Contains("from R 1,500.00", notice);
        Assert.Contains("maximum of R 1,089.75", notice);
        Assert.Contains("principal of R 10,000.00", notice);
    }

    [Fact]
    public void InitiationFee_NeverExceedsTheAbsoluteCeiling()
    {
        // The marginal formula would give R2 089.75 on R20 000; the ceiling is R1 207.50.
        var quote = Service().Quote(new LoanQuoteRequest(20_000m, 12, FirstCollection), Package());

        Assert.Equal(1_207.50m, quote.InitiationFee);
    }

    [Fact]
    public void InitiationFee_IsCapitalised_NotDeductedFromThePayout()
    {
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        // The client receives the full principal and repays principal plus fee.
        Assert.Equal(10_000m, quote.Principal);
        Assert.Equal(10_000m + quote.InitiationFee, quote.CapitalisedAmount);
        Assert.Equal(quote.CapitalisedAmount, quote.Schedule[0].OpeningBalance);
    }

    // ----------------------------------------------------------------- a worked case

    [Fact]
    public void SingleInstalment_ProducesTheExpectedRands()
    {
        // R1 000 over one month. Fee R150, capitalised R1 150.
        //   interest     1 150 x 5%     =   57.50
        //   credit life  1 150 x 0.45%  =    5.18  (5.175, rounded away from zero)
        //   service fee                 =   16.00
        //   capital                     = 1150.00
        var quote = Service().Quote(new LoanQuoteRequest(1_000m, 1, FirstCollection), Package());

        var row = Assert.Single(quote.Schedule);

        Assert.Equal(1_150m, row.OpeningBalance);
        Assert.Equal(57.50m, row.InterestPortion);
        Assert.Equal(5.18m, row.CreditLifePremium);
        Assert.Equal(16m, row.ServiceFee);
        Assert.Equal(1_150m, row.CapitalPortion);
        Assert.Equal(0m, row.ClosingBalance);
        Assert.Equal(1_228.68m, row.InstalmentTotal);

        Assert.Equal(1_228.68m, quote.TotalRepayable);
        Assert.Equal(228.68m, quote.TotalCostOfCredit);
    }

    // ----------------------------------------------------------------- schedule integrity

    [Theory]
    [InlineData(2_500, 3)]
    [InlineData(10_000, 6)]
    [InlineData(15_000, 12)]
    [InlineData(50_000, 36)]
    public void Schedule_AmortisesExactlyToZero(decimal principal, int term)
    {
        var quote = Service().Quote(new LoanQuoteRequest(principal, term, FirstCollection), Package());

        Assert.Equal(term, quote.Schedule.Count);
        Assert.Equal(0m, quote.Schedule[^1].ClosingBalance);

        // Capital repaid must equal what was advanced. Rounding lands in the last instalment.
        Assert.Equal(quote.CapitalisedAmount, quote.Schedule.Sum(r => r.CapitalPortion));
    }

    [Theory]
    [InlineData(2_500, 3)]
    [InlineData(10_000, 6)]
    [InlineData(50_000, 36)]
    public void Totals_ReconcileWithTheSchedule(decimal principal, int term)
    {
        var quote = Service().Quote(new LoanQuoteRequest(principal, term, FirstCollection), Package());

        Assert.Equal(quote.Schedule.Sum(r => r.InterestPortion), quote.TotalInterest);
        Assert.Equal(quote.Schedule.Sum(r => r.ServiceFee), quote.TotalServiceFees);
        Assert.Equal(quote.Schedule.Sum(r => r.CreditLifePremium), quote.TotalCreditLife);
        Assert.Equal(quote.Schedule.Sum(r => r.InstalmentTotal), quote.TotalRepayable);
        Assert.Equal(quote.TotalRepayable - quote.Principal, quote.TotalCostOfCredit);
    }

    [Fact]
    public void Schedule_ChainsOpeningAndClosingBalances()
    {
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        for (var i = 1; i < quote.Schedule.Count; i++)
            Assert.Equal(quote.Schedule[i - 1].ClosingBalance, quote.Schedule[i].OpeningBalance);
    }

    [Fact]
    public void Schedule_FallsDueMonthlyFromTheFirstCollectionDate()
    {
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        Assert.Equal(FirstCollection, quote.Schedule[0].DueDate);
        Assert.Equal(FirstCollection.AddMonths(5), quote.Schedule[^1].DueDate);
    }

    // ----------------------------------------------------------------- credit life

    [Fact]
    public void CreditLife_IsChargedOnTheDecliningBalance_SoItFallsEachMonth()
    {
        // This is the compliance point: the cap is per R1 000 of the deferred amount at the
        // time. Levelling the premium would overcharge in later months.
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        for (var i = 1; i < quote.Schedule.Count; i++)
            Assert.True(quote.Schedule[i].CreditLifePremium < quote.Schedule[i - 1].CreditLifePremium,
                $"Credit life did not decline at instalment {i + 1}.");
    }

    [Fact]
    public void CreditLife_NeverExceedsTheStatutoryRateOnTheOpeningBalance()
    {
        var limits = new NcaCreditLimits();
        var quote = Service(limits).Quote(new LoanQuoteRequest(10_000m, 12, FirstCollection), Package());

        foreach (var row in quote.Schedule)
        {
            var ceiling = Math.Round(row.OpeningBalance * limits.CreditLifeCeilingRate, 2,
                MidpointRounding.AwayFromZero);
            Assert.True(row.CreditLifePremium <= ceiling,
                $"Instalment {row.InstalmentNumber} charged {row.CreditLifePremium}, cap was {ceiling}.");
        }
    }

    [Fact]
    public void FirstInstalment_IsTheLargest_AndIsWhatTheMandateIsRaisedFor()
    {
        var quote = Service().Quote(new LoanQuoteRequest(10_000m, 6, FirstCollection), Package());

        Assert.Equal(quote.Schedule.Max(r => r.InstalmentTotal), quote.FirstInstalment);
        Assert.True(quote.FirstInstalment >= quote.FinalInstalment);
    }

    // ----------------------------------------------------------------- rate ceilings

    [Fact]
    public void InterestAboveTheStatutoryCeiling_IsClampedAndReported()
    {
        var quote = Service().Quote(
            new LoanQuoteRequest(10_000m, 6, FirstCollection),
            Package(interest: 0.12m));

        Assert.Equal(0.05m, quote.MonthlyInterestRate);
        Assert.Contains(quote.Notices, n => n.Contains("Interest reduced"));
    }

    [Fact]
    public void ServiceFeeAboveTheStatutoryCeiling_IsClampedAndReported()
    {
        var quote = Service().Quote(
            new LoanQuoteRequest(10_000m, 6, FirstCollection),
            Package(serviceFee: 250m));

        Assert.Equal(69m, quote.MonthlyServiceFee);
        Assert.Contains(quote.Notices, n => n.Contains("Service fee reduced"));
    }

    [Fact]
    public void CreditLifeAboveTheStatutoryCeiling_IsClampedAndReported()
    {
        var quote = Service().Quote(
            new LoanQuoteRequest(10_000m, 6, FirstCollection),
            Package(creditLife: 0.02m));

        Assert.Equal(0.0045m, quote.CreditLifeRate);
        Assert.Contains(quote.Notices, n => n.Contains("Credit life reduced"));
    }

    [Fact]
    public void AZeroInterestPackage_StillAmortises()
    {
        var quote = Service().Quote(
            new LoanQuoteRequest(6_000m, 6, FirstCollection),
            Package(interest: 0m, initiationRate: 0m));

        Assert.Equal(0m, quote.TotalInterest);
        Assert.Equal(1_000m, quote.Schedule[0].CapitalPortion);
        Assert.Equal(0m, quote.Schedule[^1].ClosingBalance);
    }

    // ----------------------------------------------------------------- package limits

    [Fact]
    public void APrincipalOutsideThePackageLimits_IsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(new LoanQuoteRequest(250_000m, 6, FirstCollection), Package()));

        Assert.Contains("lends between", ex.Message);
    }

    [Fact]
    public void ATermOutsideThePackageLimits_IsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(new LoanQuoteRequest(10_000m, 120, FirstCollection), Package()));

        Assert.Contains("runs over", ex.Message);
    }

    [Fact]
    public void ANonPositivePrincipal_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(new LoanQuoteRequest(0m, 6, FirstCollection), Package()));

    [Fact]
    public void AZeroTerm_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(new LoanQuoteRequest(10_000m, 0, FirstCollection), Package()));

    // ----------------------------------------------------------------- statutory helper

    [Theory]
    [InlineData(500, 189.75)]      // below the threshold: flat component only
    [InlineData(1_000, 189.75)]    // exactly at the threshold
    [InlineData(2_000, 289.75)]    // 189.75 + 10% of 1 000
    [InlineData(10_000, 1_089.75)] // 189.75 + 10% of 9 000
    [InlineData(20_000, 1_207.50)] // ceiling
    public void MaximumInitiationFee_FollowsTheGazettedFormula(decimal principal, decimal expected) =>
        Assert.Equal(expected, new NcaCreditLimits().MaximumInitiationFee(principal));
}
