using System.Text.Json;
using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Portal;
using AMPay.Portal.Data;

namespace AMPay.Portal.Services;

/// <summary>
/// What a loan would roughly cost, from the lender's own packages and AM-Pay's own pricing
/// engine - the same code that prices the real loan. It is still only an estimate: the
/// lender decides the package, and affordability decides whether there is a loan at all.
/// </summary>
public class EstimateService
{
    private readonly ILoanPricingService _pricing;

    public EstimateService(ILoanPricingService pricing) => _pricing = pricing;

    public record Estimate(string PackageName, decimal Instalment, decimal TotalRepayable, decimal CostOfCredit, int Term, decimal Amount);

    public record Limits(decimal MinAmount, decimal MaxAmount, int MinTerm, int MaxTerm);

    public static IReadOnlyList<PortalPackage> PackagesOf(Lender lender) =>
        JsonSerializer.Deserialize<List<PortalPackage>>(lender.PackagesJson) ?? new List<PortalPackage>();

    /// <summary>The widest range any package offers, for the form's hints and validation.</summary>
    public static Limits? RangeOf(Lender lender)
    {
        var p = PackagesOf(lender);
        return p.Count == 0
            ? null
            : new Limits(p.Min(x => x.MinLoanAmount), p.Max(x => x.MaxLoanAmount), p.Min(x => x.MinTermMonths), p.Max(x => x.MaxTermMonths));
    }

    /// <summary>
    /// Estimates on the entry-level package that allows this amount and term - the lowest
    /// tier, which a new client is most likely to be offered. Null when no package fits.
    /// </summary>
    public Estimate? For(Lender lender, decimal amount, int term, int? salaryDay)
    {
        var package = PackagesOf(lender)
            .Where(p => amount >= p.MinLoanAmount && amount <= p.MaxLoanAmount && term >= p.MinTermMonths && term <= p.MaxTermMonths)
            .OrderBy(p => p.Tier)
            .FirstOrDefault();

        if (package is null) return null;

        var quote = _pricing.Quote(
            new LoanQuoteRequest(amount, term, LoanOrigination.DefaultFirstCollectionDate(salaryDay, DateTime.Today)),
            new CreditPackage
            {
                Name = package.Name,
                Tier = package.Tier,
                MonthlyInterestRate = package.MonthlyInterestRate,
                MonthlyServiceFee = package.MonthlyServiceFee,
                InitiationFeeRate = package.InitiationFeeRate,
                CreditLifeRate = package.CreditLifeRate,
                MinLoanAmount = package.MinLoanAmount,
                MaxLoanAmount = package.MaxLoanAmount,
                MinTermMonths = package.MinTermMonths,
                MaxTermMonths = package.MaxTermMonths
            });

        return new Estimate(package.Name, quote.FirstInstalment, quote.TotalRepayable, quote.TotalCostOfCredit, term, amount);
    }
}
