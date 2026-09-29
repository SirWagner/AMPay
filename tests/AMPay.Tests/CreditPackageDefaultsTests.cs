using AMPay.Domain.Credit;
using AMPay.Domain.Enums;

namespace AMPay.Tests;

public class CreditPackageDefaultsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void EveryCustomerStartsWithOnePackagePerTier()
    {
        var packages = CreditPackageDefaults.For(Tenant);

        Assert.Equal(new[] { CreditTier.Regular, CreditTier.Gold, CreditTier.Premium },
            packages.Select(p => p.Tier));
        Assert.All(packages, p => Assert.Equal(Tenant, p.TenantId));
        Assert.All(packages, p => Assert.True(p.IsActive));
    }

    [Fact]
    public void TheDefaults_AreWithinTheStatutoryCeilings()
    {
        var limits = new NcaCreditLimits();

        Assert.All(CreditPackageDefaults.For(Tenant), p =>
        {
            Assert.True(p.MonthlyInterestRate <= limits.MonthlyInterestCeiling, $"{p.Name} interest");
            Assert.True(p.MonthlyServiceFee <= limits.MonthlyServiceFeeCeiling, $"{p.Name} service fee");
            Assert.True(p.CreditLifeRate <= limits.CreditLifeCeilingRate, $"{p.Name} credit life");
            Assert.True(p.MinLoanAmount < p.MaxLoanAmount, $"{p.Name} loan range");
            Assert.True(p.MinTermMonths <= p.MaxTermMonths, $"{p.Name} term range");
        });
    }

    [Fact]
    public void EachCallBuildsNewRows_SoTwoCustomersNeverShareAPackage()
    {
        var a = CreditPackageDefaults.For(Guid.NewGuid());
        var b = CreditPackageDefaults.For(Guid.NewGuid());

        Assert.Empty(a.Select(p => p.Id).Intersect(b.Select(p => p.Id)));
    }

    [Fact]
    public void MissingFor_ACustomerWithNothing_IsAllThree() =>
        Assert.Equal(3, CreditPackageDefaults.MissingFor(Tenant, Array.Empty<(CreditTier, string)>()).Count);

    [Fact]
    public void MissingFor_OnlyAddsAbsentTiers_SoARepricedPackageIsNeverReplaced()
    {
        var missing = CreditPackageDefaults.MissingFor(Tenant, new[] { (CreditTier.Gold, "Gold") });

        Assert.Equal(new[] { CreditTier.Regular, CreditTier.Premium }, missing.Select(p => p.Tier));
    }

    [Fact]
    public void MissingFor_ACompleteCustomer_IsNothing() =>
        Assert.Empty(CreditPackageDefaults.MissingFor(Tenant, new[]
        {
            (CreditTier.Regular, "Regular"), (CreditTier.Gold, "Gold"), (CreditTier.Premium, "Premium")
        }));

    [Fact]
    public void MissingFor_ATierCoveredByACustomPackage_IsNotRefilled()
    {
        // Two custom Gold packages under their own names: Gold is covered, so the standard
        // "Gold" is not added alongside them.
        var missing = CreditPackageDefaults.MissingFor(Tenant, new[]
        {
            (CreditTier.Gold, "Public servants"), (CreditTier.Gold, "Mining payroll")
        });

        Assert.Equal(new[] { "Regular", "Premium" }, missing.Select(p => p.Name));
    }

    [Fact]
    public void MissingFor_SkipsAStandardPackageWhoseNameIsTaken_RegardlessOfCase()
    {
        // A custom Gold-tier package called "regular". Adding the standard Regular package
        // would break the unique name index and fail the whole startup, so it is skipped.
        var missing = CreditPackageDefaults.MissingFor(Tenant, new[] { (CreditTier.Gold, "regular") });

        Assert.Equal(new[] { "Premium" }, missing.Select(p => p.Name));
    }
}
