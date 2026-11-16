using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Tests;

/// <summary>Wording changed for one borrower, and the fields wording may refer to.</summary>
public class LoanWordingTests
{
    private const string Kind = nameof(ContractTemplateKind.CreditAgreementTerms);

    [Fact]
    public async Task StandardWording_IsUsedWhenNothingWasChangedForTheClient()
    {
        var (db, svc, loan) = await ContractTests.ArrangeAsync();

        var s = ContractService.ReadSnapshot(await svc.IssueAsync(loan.Id, "user-1"));

        var template = await db.ContractTemplates.SingleAsync(t => t.Kind == ContractTemplateKind.CreditAgreementTerms);
        var terms = s.Terms.Single(t => t.Kind == Kind);
        Assert.False(terms.Custom);
        Assert.Equal(template.Title, terms.Title);
        Assert.Equal(nameof(ContractTemplateKind.BorrowerDeclarations), s.Terms[0].Kind);
    }

    [Fact]
    public async Task WordingChangedForOneLoan_ReplacesThatSectionOnly_AndLeavesTheStandardAlone()
    {
        var (db, svc, loan) = await ContractTests.ArrangeAsync();
        db.LoanWordings.Add(new LoanWording
        {
            LoanId = loan.Id,
            Kind = ContractTemplateKind.CreditAgreementTerms,
            Title = "Terms agreed with this borrower",
            Body = "1. A settlement discount of R 100.00 applies if the loan is settled within 30 days.",
            Reason = "Agreed at the branch",
            IsApproved = true
        });
        await db.SaveChangesAsync();

        var s = ContractService.ReadSnapshot(await svc.IssueAsync(loan.Id, "user-1"));

        var terms = s.Terms.Single(t => t.Kind == Kind);
        Assert.True(terms.Custom);
        Assert.Equal("Terms agreed with this borrower", terms.Title);
        Assert.Contains("settlement discount", terms.Body);
        Assert.All(s.Terms.Where(t => t.Kind != Kind), t => Assert.False(t.Custom));

        var after = await db.ContractTemplates.AsNoTracking().SingleAsync(t => t.Kind == ContractTemplateKind.CreditAgreementTerms);
        Assert.DoesNotContain("settlement discount", after.Body);
    }

    [Fact]
    public async Task AContractIsADraft_UntilTheStandardAndTheOneOffWordingAreBothApproved()
    {
        var (db, svc, loan) = await ContractTests.ArrangeAsync();
        await svc.EnsureTemplatesAsync(loan.TenantId);
        foreach (var t in await db.ContractTemplates.ToListAsync()) t.IsApproved = true;
        var own = new LoanWording
        {
            LoanId = loan.Id, Kind = ContractTemplateKind.DebitOrderAuthorisation,
            Title = "Debit order", Body = "1. Collected on the 25th of each month.", Reason = "Pay day", IsApproved = false
        };
        db.LoanWordings.Add(own);
        await db.SaveChangesAsync();

        Assert.False((await svc.IssueAsync(loan.Id, "user-1")).TemplatesApproved);

        own.IsApproved = true;
        await db.SaveChangesAsync();

        Assert.True((await svc.IssueAsync(loan.Id, "user-1")).TemplatesApproved);
    }

    [Fact]
    public async Task Fields_AreFilledInAtIssue_AndFrozenIntoTheContract()
    {
        var (db, svc, loan) = await ContractTests.ArrangeAsync();
        await svc.EnsureTemplatesAsync(loan.TenantId);
        var template = await db.ContractTemplates.SingleAsync(t => t.Kind == ContractTemplateKind.CreditAgreementTerms);
        template.Body = "1. {{Lender.RegisteredName}} lends {{ Cost.Principal }} to {{Client.Surname}} at {{Rate.MonthlyPct}} a month, " +
                        "repayable as {{Schedule.InstalmentCount}} instalment(s). Ref {{Loan.ContractNo}}.";
        await db.SaveChangesAsync();

        var contract = await svc.IssueAsync(loan.Id, "user-1");
        var body = ContractService.ReadSnapshot(contract).Terms.Single(t => t.Kind == Kind).Body;

        Assert.DoesNotContain("{{", body);
        Assert.Contains("Albatross Money (Pty) Ltd lends " + ContractFormat.Money(loan.Principal), body);
        Assert.Contains(ContractFormat.Percent(loan.MonthlyInterestRate) + " a month", body);
        Assert.Contains($"repayable as {loan.NumberOfInstalments} instalment(s)", body);
        Assert.Contains("Ref " + contract.Reference, body);
    }

    [Fact]
    public void UnknownFields_AreCaughtBeforeSaving_AndNeverSilentlyDropped()
    {
        const string text = "Pay {{Lender.RegisteredName}} or {{Lender.Nmae}} by {{Due.Date}}. {{lender.tradingname}} is fine.";

        Assert.Equal(new[] { "Lender.Nmae", "Due.Date" }, ContractMergeFields.Unknown(text));
        Assert.Empty(ContractMergeFields.Unknown("No fields here, and {single} braces are just text."));

        var resolved = ContractMergeFields.Resolve(text, new Dictionary<string, string?>
        {
            ["Lender.RegisteredName"] = "Test Lender", ["Lender.TradingName"] = "TL"
        });
        Assert.Equal("Pay Test Lender or {{Lender.Nmae}} by {{Due.Date}}. TL is fine.", resolved);
    }

    [Fact]
    public void AFieldWithNoValue_PrintsAsNothing_NotAsNone()
    {
        var resolved = ContractMergeFields.Resolve("Language: {{Client.Language}}.", new Dictionary<string, string?> { ["Client.Language"] = null });

        Assert.Equal("Language: .", resolved);
    }

    [Fact]
    public void EveryDefaultSection_UsesOnlyFieldsThatExist()
    {
        foreach (var t in ContractTemplateDefaults.For(Guid.NewGuid()))
            Assert.Empty(ContractMergeFields.Unknown(t.Body));
    }
}
