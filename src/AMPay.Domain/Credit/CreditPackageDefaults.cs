using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Credit;

/// <summary>
/// The standard Regular / Gold / Premium price list every customer starts with.
/// <para>
/// One definition, used when a customer is created, when the application seeds existing
/// customers that have none, and from the Credit packages page. A customer without any
/// package cannot lend at all, so no route to a new customer may skip this.
/// </para>
/// <para>
/// Only the tier is fixed. Every rate is a starting point for the customer to reprice,
/// and each is clamped to the statutory maximum at quote time regardless. Interest sits at
/// the 5% per month ceiling on Regular and steps down for better tiers; the service fee
/// and lending limits move with it. The initiation rate is the same across all three.
/// </para>
/// </summary>
public static class CreditPackageDefaults
{
    public static IReadOnlyList<CreditPackage> For(Guid tenantId) => new[]
    {
        new CreditPackage
        {
            TenantId = tenantId,
            Tier = CreditTier.Regular,
            Name = "Regular",
            Description = "Entry tier. Priced at the statutory maximum rate.",
            MonthlyInterestRate = 0.05m,
            MonthlyServiceFee = 16m,
            InitiationFeeRate = 0.15m,
            CreditLifeRate = 0.0045m,
            MinLoanAmount = 500m,
            MaxLoanAmount = 8_000m,
            MinTermMonths = 1,
            MaxTermMonths = 6
        },
        new CreditPackage
        {
            TenantId = tenantId,
            Tier = CreditTier.Gold,
            Name = "Gold",
            Description = "Repeat clients in good standing. Lower rate, longer terms.",
            MonthlyInterestRate = 0.035m,
            MonthlyServiceFee = 16m,
            InitiationFeeRate = 0.15m,
            CreditLifeRate = 0.0045m,
            MinLoanAmount = 1_000m,
            MaxLoanAmount = 25_000m,
            MinTermMonths = 3,
            MaxTermMonths = 12
        },
        new CreditPackage
        {
            TenantId = tenantId,
            Tier = CreditTier.Premium,
            Name = "Premium",
            Description = "Best rate and highest limits. Reserved for the strongest books.",
            MonthlyInterestRate = 0.025m,
            MonthlyServiceFee = 10m,
            InitiationFeeRate = 0.15m,
            CreditLifeRate = 0.0045m,
            MinLoanAmount = 5_000m,
            MaxLoanAmount = 100_000m,
            MinTermMonths = 6,
            MaxTermMonths = 36
        }
    };

    /// <summary>
    /// The standard packages this customer is missing: one for each tier that has no
    /// package at all. Adding only what is absent makes this safe to run more than once,
    /// and it never touches a package a customer has already repriced.
    /// <para>
    /// A standard package whose name is already taken - say the customer called a custom
    /// Gold package "Regular" - is skipped rather than added. Names are unique per
    /// customer, and adding it anyway would fail the whole startup.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CreditPackage> MissingFor(
        Guid tenantId, IEnumerable<(CreditTier Tier, string Name)> existing)
    {
        var list = existing.ToList();
        var tiers = list.Select(e => e.Tier).ToHashSet();
        var names = list.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return For(tenantId)
            .Where(p => !tiers.Contains(p.Tier) && !names.Contains(p.Name))
            .ToList();
    }
}
