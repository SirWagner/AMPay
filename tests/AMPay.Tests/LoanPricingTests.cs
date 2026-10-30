using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Tests;

public class LoanPricingTests
{
    private static readonly DateTime FirstCollection = new(2026, 10, 25);

    /// <summary>Caps with an unsecured interest ceiling set, for loans above the short-term limits.</summary>
    private static readonly RegulatoryCaps WithUnsecured = RegulatoryCaps.Statutory with { UnsecuredAnnualRateMax = 0.60m };

    private static LoanPricingService Service() => new();

    private static LoanQuoteRequest Request(decimal principal, int term, bool vat = false, int prior = 0,
        RegulatoryCaps? caps = null, FeeOverride? fee = null, decimal funeral = 0m, decimal thirdParty = 0m) =>
        new(principal, term, FirstCollection)
        {
            Caps = caps ?? WithUnsecured,
            VatRegistered = vat,
            PriorShortTermLoansThisYear = prior,
            Override = fee,
            FuneralCover = funeral,
            ThirdPartyPayment = thirdParty
        };

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

    // ----------------------------------------------------------------- initiation fee (excl. VAT)

    [Fact]
    public void InitiationFee_BelowStatutoryMaximum_IsChargedInFull()
    {
        // 15% of R1 000 is R150, under the R165 flat cap.
        var quote = Service().Quote(Request(1_000m, 1), Package());

        Assert.Equal(150m, quote.InitiationFee);
        Assert.Empty(quote.Notices);
    }

    [Fact]
    public void InitiationFee_AboveStatutoryMaximum_IsClampedAndReported()
    {
        // 15% of R2 000 is R300. The cap is R165 + 10% of R1 000 = R265.
        var quote = Service().Quote(Request(2_000m, 2), Package());

        Assert.Equal(265m, quote.InitiationFee);
        var notice = Assert.Single(quote.Notices);
        Assert.Contains("from R 300.00", notice);
        Assert.Contains("maximum of R 265.00", notice);
        Assert.Contains("principal of R 2,000.00", notice);
    }

    [Fact]
    public void InitiationFee_NeverExceedsTheAbsoluteCeiling() =>
        Assert.Equal(1_050m, Service().Quote(Request(20_000m, 12), Package()).InitiationFee);

    [Theory]
    [InlineData(500, 165)]
    [InlineData(1_000, 165)]
    [InlineData(2_000, 265)]
    [InlineData(9_000, 965)]
    [InlineData(10_000, 1_050)] // 165 + 900 = 1 065, held at the R1 050 ceiling
    public void MaximumInitiationFee_FollowsTheGazettedFormula(decimal principal, decimal expected) =>
        Assert.Equal(expected, RegulatoryCaps.Statutory.MaximumInitiationFee(principal));

    [Fact]
    public void InitiationFee_IsCapitalised_NotDeductedFromThePayout()
    {
        var quote = Service().Quote(Request(5_000m, 6), Package());

        Assert.Equal(5_000m, quote.Principal);
        Assert.Equal(5_000m + quote.InitiationFee, quote.CapitalisedAmount);
        Assert.Equal(quote.CapitalisedAmount, quote.Schedule[0].OpeningBalance);
    }

    // ----------------------------------------------------------------- a worked case

    [Fact]
    public void SingleInstalment_ProducesTheExpectedRands()
    {
        // R1 000 over one month, no VAT. Fee R150, capitalised R1 150.
        //   interest     1 150 x 5%     =   57.50
        //   credit life  1 150 x 0.45%  =    5.18  (5.175, rounded away from zero)
        //   service fee                 =   16.00
        var quote = Service().Quote(Request(1_000m, 1), Package());

        var row = Assert.Single(quote.Schedule);
        Assert.Equal(57.50m, row.InterestPortion);
        Assert.Equal(5.18m, row.CreditLifePremium);
        Assert.Equal(1_150m, row.CapitalPortion);
        Assert.Equal(1_228.68m, row.InstalmentTotal);
        Assert.Equal(1_228.68m, quote.TotalRepayable);
        Assert.Equal(228.68m, quote.TotalCostOfCredit);
    }

    // ----------------------------------------------------------------- VAT (f.5)

    [Fact]
    public void AVatRegisteredLender_AddsVatOnTheInitiationAndServiceFees_NotOnCreditLife()
    {
        // Fee R150 (VAT R22.50) and R16 a month for 3 months (VAT R2.40 each): 15% of R198.
        var quote = Service().Quote(Request(1_000m, 3, vat: true), Package());

        Assert.Equal(29.70m, quote.TotalVat);
        Assert.Equal(0.15m, quote.VatRate);
        Assert.All(quote.Schedule, r => Assert.Equal(2.40m, r.VatPortion));
        Assert.Equal(1_000m + 150m + 22.50m, quote.CapitalisedAmount); // the fee's VAT is financed with it
    }

    [Fact]
    public void ALenderNotRegisteredForVat_ChargesNone()
    {
        var quote = Service().Quote(Request(1_000m, 3), Package());
        Assert.Equal(0m, quote.TotalVat);
        Assert.All(quote.Schedule, r => Assert.Equal(0m, r.VatPortion));
    }

    // ----------------------------------------------------------------- a-l arithmetic

    [Theory]
    [InlineData(2_000, 2, true)]
    [InlineData(2_500, 3, false)]
    [InlineData(8_000, 6, true)]
    [InlineData(15_000, 12, true)]
    [InlineData(50_000, 36, false)]
    public void TheCostElementsAddUp_GEqualsDPlusE_ToTheCent(decimal principal, int term, bool vat)
    {
        var q = Service().Quote(Request(principal, term, vat), Package());

        Assert.Equal(q.TotalLoanAmount, principal);
        Assert.Equal(q.TotalCreditLife + q.InitiationFee + q.TotalServiceFees + q.TotalInterest + q.TotalVat, q.TotalCostOfCredit);
        Assert.Equal(q.TotalLoanAmount + q.TotalCostOfCredit, q.TotalRepayable);
        Assert.Equal(q.Schedule.Sum(r => r.InstalmentTotal), q.TotalRepayable);
        Assert.Equal(Math.Round(q.TotalRepayable / q.TotalLoanAmount, 2, MidpointRounding.AwayFromZero), q.CreditCostMultiple);
        Assert.Equal(q.MonthlyInterestRate, q.PenaltyMonthlyRate);
    }

    [Fact]
    public void FuneralCoverAndAThirdPartyPayment_AreFinanced_InTheTotalLoanAmount()
    {
        var q = Service().Quote(Request(2_000m, 2, funeral: 150m, thirdParty: 250m), Package());

        Assert.Equal(2_400m, q.TotalLoanAmount);                       // d = a + b + c
        Assert.Equal(2_400m + q.InitiationFee, q.CapitalisedAmount);
        Assert.Equal(265m, q.InitiationFee);                           // the fee cap is on the principal (a)
        Assert.Equal(q.TotalLoanAmount + q.TotalCostOfCredit, q.TotalRepayable);
    }

    // ----------------------------------------------------------------- level instalments

    [Theory]
    [InlineData(2_500, 3)]
    [InlineData(8_000, 6)]
    [InlineData(50_000, 36)]
    public void Instalments_AreLevel_TheLastAbsorbingTheRounding(decimal principal, int term)
    {
        var q = Service().Quote(Request(principal, term, vat: true), Package());

        Assert.All(q.Schedule.Take(term - 1), r => Assert.Equal(q.BaseInstalment, r.InstalmentTotal));
        Assert.True(Math.Abs(q.FinalInstalment - q.BaseInstalment) < 1m,
            $"Final instalment {q.FinalInstalment} strays from {q.BaseInstalment}.");
        Assert.Equal(0m, q.Schedule[^1].ClosingBalance);
        Assert.Equal(q.CapitalisedAmount, q.Schedule.Sum(r => r.CapitalPortion));
    }

    [Fact]
    public void Schedule_ChainsBalances_AndFallsDueMonthly()
    {
        var q = Service().Quote(Request(8_000m, 6), Package());

        for (var i = 1; i < q.Schedule.Count; i++)
            Assert.Equal(q.Schedule[i - 1].ClosingBalance, q.Schedule[i].OpeningBalance);
        Assert.Equal(FirstCollection, q.Schedule[0].DueDate);
        Assert.Equal(FirstCollection.AddMonths(5), q.Schedule[^1].DueDate);
    }

    // ----------------------------------------------------------------- credit life

    [Fact]
    public void CreditLife_IsChargedOnTheDecliningBalance_WithinTheCap()
    {
        var q = Service().Quote(Request(8_000m, 6), Package());

        for (var i = 1; i < q.Schedule.Count; i++)
            Assert.True(q.Schedule[i].CreditLifePremium < q.Schedule[i - 1].CreditLifePremium);
        Assert.All(q.Schedule, r => Assert.True(r.CreditLifePremium <=
            Math.Round(r.OpeningBalance * RegulatoryCaps.Statutory.CreditLifeMonthlyRateMax, 2, MidpointRounding.AwayFromZero)));
    }

    // ----------------------------------------------------------------- interest ceilings

    [Fact]
    public void AShortTermLoan_IsCappedAtFivePercent_OnTheFirstLoanOfTheYear()
    {
        var q = Service().Quote(Request(2_000m, 2), Package(interest: 0.12m));
        Assert.Equal(0.05m, q.MonthlyInterestRate);
        Assert.Equal("Short-term Credit Transaction", q.ProductType);
        Assert.Contains(q.Notices, n => n.Contains("Interest reduced"));
    }

    [Fact]
    public void ALaterShortTermLoanInTheSameYear_IsCappedAtThreePercent()
    {
        var q = Service().Quote(Request(2_000m, 2, prior: 1), Package(interest: 0.05m));
        Assert.Equal(0.03m, q.MonthlyInterestRate);
        Assert.Contains(q.Notices, n => n.Contains("later short-term loan this calendar year"));
    }

    [Fact]
    public void AnUnsecuredLoan_UsesTheAnnualCap_AndIsRefusedWhileItIsNotSet()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Service().Quote(Request(10_000m, 6, caps: RegulatoryCaps.Statutory), Package()));
        Assert.Contains("unsecured interest cap is not set", ex.Message);

        var q = Service().Quote(Request(10_000m, 6, caps: RegulatoryCaps.Statutory with { UnsecuredAnnualRateMax = 0.28m }),
            Package(interest: 0.05m));
        Assert.Equal("Unsecured Credit Transaction", q.ProductType);
        Assert.Equal(0.28m / 12m, q.MonthlyInterestRate);
    }

    [Fact]
    public void ServiceFeeAndCreditLife_AboveTheirCaps_AreClampedAndReported()
    {
        var q = Service().Quote(Request(2_000m, 2), Package(serviceFee: 250m, creditLife: 0.02m));
        Assert.Equal(60m, q.MonthlyServiceFee);
        Assert.Equal(0.0045m, q.CreditLifeRate);
        Assert.Contains(q.Notices, n => n.Contains("Service fee reduced"));
        Assert.Contains(q.Notices, n => n.Contains("Credit life reduced"));
    }

    // ----------------------------------------------------------------- fee overrides

    [Fact]
    public void AnOverrideUpToTheCap_IsApplied_AndRecorded()
    {
        var fee = new FeeOverride(InitiationFee: 200m, MonthlyServiceFee: 50m, MonthlyInterestRate: 0.04m, Reason: "Loyal client");
        var q = Service().Quote(Request(2_000m, 2, fee: fee), Package());

        Assert.Equal(200m, q.InitiationFee);
        Assert.Equal(50m, q.MonthlyServiceFee);
        Assert.Equal(0.04m, q.MonthlyInterestRate);
        Assert.Same(fee, q.AppliedOverride);
        Assert.Empty(q.Notices);
    }

    [Fact]
    public void AnOverrideAboveTheCap_IsRefused_ButACentOfRoundingIsTolerated()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(Request(2_000m, 2, fee: new FeeOverride(265.02m, null, null, "x")), Package()));
        Assert.Contains("above the statutory maximum", ex.Message);

        Assert.Equal(265.01m, Service().Quote(Request(2_000m, 2, fee: new FeeOverride(265.01m, null, null, "x")), Package()).InitiationFee);
    }

    // ----------------------------------------------------------------- odd cases and limits

    [Fact]
    public void AZeroChargePackage_AmortisesEvenly()
    {
        var q = Service().Quote(Request(6_000m, 6), Package(interest: 0m, serviceFee: 0m, initiationRate: 0m, creditLife: 0m));

        Assert.Equal(0m, q.TotalCostOfCredit);
        Assert.All(q.Schedule, r => Assert.Equal(1_000m, r.InstalmentTotal));
    }

    [Fact]
    public void APrincipalOutsideThePackageLimits_IsRefused() =>
        Assert.Contains("lends between", Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(Request(250_000m, 6), Package())).Message);

    [Fact]
    public void ATermOutsideThePackageLimits_IsRefused() =>
        Assert.Contains("runs over", Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Quote(Request(10_000m, 120), Package())).Message);

    [Fact]
    public void ANonPositivePrincipal_OrAZeroTerm_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Service().Quote(Request(0m, 6), Package()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Service().Quote(Request(10_000m, 0), Package()));
    }
}
