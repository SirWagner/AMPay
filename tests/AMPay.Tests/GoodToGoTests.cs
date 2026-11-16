using System.IO.Compression;
using System.Text;
using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Validation;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Credit;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

public class GoodToGoTests
{
    private static readonly DateTime Today = new(2026, 11, 13);

    /// <summary>A client with nothing wrong: every check passes.</summary>
    private static GoodToGoInput Clean() => new()
    {
        IsSaIdNumber = true,
        IdNumberValid = true,
        AgeYears = 41,
        PopiaConsent = true,
        HasMobileNumber = true,
        EmploymentType = "Permanent",
        EmployedSince = new DateTime(2020, 3, 1),
        NetMonthlyIncome = 15_000m,
        BudgetCaptured = true,
        NetOfNet = 6_000m,
        HasBankAccount = true,
        BankAccountVerified = true,
        LifeVerification = VerificationResult.Confirmed,
        EmploymentVerification = VerificationResult.Confirmed,
        Today = Today
    };

    // ----------------------------------------------------------------- the rules

    [Fact]
    public void CleanClient_IsGoodToGo_WithFullScore_AndFirstTime()
    {
        var result = GoodToGo.Evaluate(Clean());

        Assert.Equal(ScreeningOutcome.GoodToGo, result.Outcome);
        Assert.Equal(100, result.Score);
        Assert.True(result.IsFirstTimeApplicant);
        Assert.All(result.Checks, c => Assert.Equal(CheckStatus.Pass, c.Status));
    }

    [Fact]
    public void NothingCapturedYet_IsReview_NotARefusal()
    {
        var result = GoodToGo.Evaluate(new GoodToGoInput { IdNumberValid = true, AgeYears = 30, Today = Today });

        Assert.Equal(ScreeningOutcome.Review, result.Outcome);
        Assert.DoesNotContain(result.Checks, c => c.Status == CheckStatus.Fail);
        Assert.Contains(result.Checks, c => c is { Name: "Home Affairs", Status: CheckStatus.Missing });
        Assert.Contains(result.Checks, c => c is { Name: "Budget", Status: CheckStatus.Missing });
    }

    [Theory]
    [InlineData("Unemployed")]
    [InlineData("unemployed")]
    public void Unemployed_IsNotGoodToGo(string type)
    {
        var result = GoodToGo.Evaluate(Clean() with { EmploymentType = type });

        Assert.Equal(ScreeningOutcome.NotGoodToGo, result.Outcome);
        Assert.Contains(result.Checks, c => c is { Name: "Employment", Status: CheckStatus.Fail });
    }

    [Fact]
    public void Under18_Deceased_AndNothingLeft_EachRefuse()
    {
        Assert.Equal(ScreeningOutcome.NotGoodToGo, GoodToGo.Evaluate(Clean() with { AgeYears = 17 }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo,
            GoodToGo.Evaluate(Clean() with { LifeVerification = VerificationResult.Failed }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo, GoodToGo.Evaluate(Clean() with { NetOfNet = 0m }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo,
            GoodToGo.Evaluate(Clean() with { EmploymentVerification = VerificationResult.Failed }).Outcome);
    }

    [Fact]
    public void Warnings_MakeItReview_AndCostPoints()
    {
        var result = GoodToGo.Evaluate(Clean() with
        {
            EmploymentType = "Contract",
            ContractExpiryDate = Today.AddMonths(2),
            BankAccountVerified = false,
            NetOfNet = 300m
        });

        Assert.Equal(ScreeningOutcome.Review, result.Outcome);
        Assert.Equal(4, result.Checks.Count(c => c.Status == CheckStatus.Warn));
        Assert.Equal(100 - 4 * 8, result.Score);
    }

    [Fact]
    public void ContractAlreadyEnded_Refuses()
    {
        var result = GoodToGo.Evaluate(Clean() with { EmploymentType = "Contract", ContractExpiryDate = Today.AddDays(-1) });

        Assert.Equal(ScreeningOutcome.NotGoodToGo, result.Outcome);
    }

    [Fact]
    public void NewInTheJob_IsAWarning_ButAPensionerIsNot()
    {
        var recent = Today.AddMonths(-1);

        Assert.Contains(GoodToGo.Evaluate(Clean() with { EmployedSince = recent }).Checks,
            c => c is { Name: "Time in job", Status: CheckStatus.Warn });
        Assert.DoesNotContain(GoodToGo.Evaluate(Clean() with { EmploymentType = "Pensioner", EmployedSince = recent }).Checks,
            c => c.Name == "Time in job");
    }

    [Fact]
    public void HistoryWithTheLender_DecidesFirstTimeAndArrears()
    {
        var repaid = GoodToGo.Evaluate(Clean() with { PreviousLoans = 2 });
        Assert.False(repaid.IsFirstTimeApplicant);
        Assert.Equal(ScreeningOutcome.GoodToGo, repaid.Outcome);

        Assert.Equal(ScreeningOutcome.Review,
            GoodToGo.Evaluate(Clean() with { PreviousLoans = 1, ActiveLoans = 1, DaysInArrears = 5 }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo,
            GoodToGo.Evaluate(Clean() with { PreviousLoans = 1, ActiveLoans = 1, DaysInArrears = 31 }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo,
            GoodToGo.Evaluate(Clean() with { PreviousLoans = 1, WrittenOffLoans = 1 }).Outcome);
    }

    [Fact]
    public void StatementFlag_FeedsTheOutcome_OnlyWhenAssessed()
    {
        Assert.DoesNotContain(GoodToGo.Evaluate(Clean()).Checks, c => c.Name == "Bank statement");
        Assert.Equal(ScreeningOutcome.Review, GoodToGo.Evaluate(Clean() with { StatementFlag = StatementFlag.Warning }).Outcome);
        Assert.Equal(ScreeningOutcome.NotGoodToGo, GoodToGo.Evaluate(Clean() with { StatementFlag = StatementFlag.Bad }).Outcome);
    }

    [Fact]
    public void Checks_SurviveBeingStoredAsText()
    {
        var result = GoodToGo.Evaluate(Clean() with { EmploymentType = "Unemployed" });

        var read = GoodToGoResult.ParseChecks(result.SerialisedChecks);

        Assert.Equal(result.Checks, read);
    }

    [Fact]
    public void Age_CountsBirthdaysNotYears()
    {
        Assert.Equal(17, GoodToGo.AgeOn(new DateTime(2008, 11, 14), Today));
        Assert.Equal(18, GoodToGo.AgeOn(new DateTime(2008, 11, 13), Today));
    }

    // ----------------------------------------------------------------- from the client's file

    [Fact]
    public async Task Evaluate_ReadsTheFile_AndWritesOneRowWhileNothingChanges()
    {
        var (db, service, client) = await ArrangeAsync();

        var first = await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "tester", Today);
        var second = await service.EvaluateAsync(client.Id, ScreeningTrigger.DetailsChanged, "tester", Today.AddDays(1));

        Assert.Equal(ScreeningOutcome.GoodToGo, first!.Outcome);
        Assert.True(first.AllowsCreditCheck);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(Today.AddDays(1), second.CheckedUtc);
        Assert.Equal(1, await db.ClientScreenings.CountAsync());
    }

    [Fact]
    public async Task Evaluate_WritesANewRowWhenTheFileChanges_AndTheOverrideDoesNotCarryOver()
    {
        var (db, service, client) = await ArrangeAsync(c => c.BankAccounts.Single().AvsVerified = false);

        var review = await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "tester", Today);
        Assert.Equal(ScreeningOutcome.Review, review!.Outcome);
        Assert.False(review.AllowsCreditCheck);

        review.OverrideReason = "Seen the bank letter myself.";
        await db.SaveChangesAsync();
        Assert.True(review.AllowsCreditCheck);

        (await db.ClientEmployments.SingleAsync()).EmploymentType = "Unemployed";
        await db.SaveChangesAsync();
        var after = await service.EvaluateAsync(client.Id, ScreeningTrigger.DetailsChanged, "tester", Today.AddDays(1));

        Assert.NotEqual(review.Id, after!.Id);
        Assert.Equal(ScreeningOutcome.NotGoodToGo, after.Outcome);
        Assert.Null(after.OverrideReason);
        Assert.False(after.AllowsCreditCheck);
        Assert.Equal(2, await db.ClientScreenings.CountAsync());
    }

    [Fact]
    public async Task Arrears_ComeFromTheOldestUnpaidInstalmentOfARunningLoan()
    {
        var (db, service, client) = await ArrangeAsync();
        var loan = new Loan { TenantId = client.TenantId, ClientId = client.Id, LoanNumber = "L1", Status = LoanStatus.Disbursed };
        db.Loans.Add(loan);
        db.LoanScheduleEntries.AddRange(
            new LoanScheduleEntry { LoanId = loan.Id, InstalmentNumber = 1, DueDate = Today.AddDays(-70), InstalmentTotal = 500m, PaidAmount = 500m },
            new LoanScheduleEntry { LoanId = loan.Id, InstalmentNumber = 2, DueDate = Today.AddDays(-40), InstalmentTotal = 500m, PaidAmount = 100m },
            new LoanScheduleEntry { LoanId = loan.Id, InstalmentNumber = 3, DueDate = Today.AddDays(20), InstalmentTotal = 500m });
        await db.SaveChangesAsync();

        var input = await service.BuildInputAsync(client.Id, Today);

        Assert.Equal(40, input!.DaysInArrears);
        Assert.Equal(1, input.ActiveLoans);
        Assert.Equal(1, input.PreviousLoans);
        Assert.Equal(ScreeningOutcome.NotGoodToGo, GoodToGo.Evaluate(input).Outcome);
    }

    [Fact]
    public async Task AnApplicationThatWasNeverPaidOut_IsNotHistory()
    {
        var (db, service, client) = await ArrangeAsync();
        db.Loans.Add(new Loan { TenantId = client.TenantId, ClientId = client.Id, LoanNumber = "L1", Status = LoanStatus.Declined });
        await db.SaveChangesAsync();

        var input = await service.BuildInputAsync(client.Id, Today);

        Assert.Equal(0, input!.PreviousLoans);
        Assert.True(GoodToGo.Evaluate(input).IsFirstTimeApplicant);
    }

    [Fact]
    public async Task StaleResults_AreRecheckedByThemselves_AndAWorseFlagRaisesAFollowUp()
    {
        var (db, service, client) = await ArrangeAsync(c =>
        {
            c.Employment!.EmploymentType = "Contract";
            c.Employment.ContractExpiryDate = Today.AddDays(20);
        });
        var first = await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "tester", Today);
        Assert.Equal(ScreeningOutcome.Review, first!.Outcome);

        // Too soon: nothing is due.
        Assert.Equal(0, await service.RecheckStaleAsync(null, Today.AddDays(10)));

        // 31 days on, the contract has ended without anyone touching the file.
        var rechecked = await service.RecheckStaleAsync(null, Today.AddDays(31));

        Assert.Equal(1, rechecked);
        var latest = await service.LatestAsync(client.Id);
        Assert.Equal(ScreeningOutcome.NotGoodToGo, latest!.Outcome);
        Assert.Equal(ScreeningTrigger.Scheduled, latest.Trigger);
        var note = await db.ClientNotes.SingleAsync();
        Assert.True(note.FollowUpOpen);
        Assert.Contains("Review to Not good to go", note.Body);
    }

    [Fact]
    public async Task RecheckPeriodOfZero_SwitchesTheAutomaticRecheckOff()
    {
        var (db, service, client) = await ArrangeAsync();
        await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "tester", Today);
        (await db.Tenants.SingleAsync()).ScreeningRecheckDays = 0;
        await db.SaveChangesAsync();

        Assert.Equal(0, await service.RecheckStaleAsync(null, Today.AddDays(400)));
    }

    [Fact]
    public async Task SimulatedAnswers_AreNamedAsSuch_AndASimulatedStatementMovesNothing()
    {
        var (db, service, client) = await ArrangeAsync(c => c.Verifications.First(v => v.Kind == VerificationKind.Life).Simulated = true);
        db.BankStatementAssessments.Add(new BankStatementAssessment
        {
            ClientId = client.Id, DocumentId = Guid.NewGuid(), Flag = StatementFlag.Bad, Summary = "stub", Model = "stub", Simulated = true
        });
        await db.SaveChangesAsync();

        var result = GoodToGo.Evaluate((await service.BuildInputAsync(client.Id, Today))!);

        Assert.Equal(ScreeningOutcome.GoodToGo, result.Outcome);
        Assert.DoesNotContain(result.Checks, c => c.Name == "Bank statement");
        Assert.Contains("Simulated", result.Checks.Single(c => c.Name == "Home Affairs").Detail);
    }

    [Fact]
    public async Task DocumentsWaitForIncomeAndABudget()
    {
        var (db, service, client) = await ArrangeAsync();
        Assert.True(await service.AffordabilityCapturedAsync(client.Id));

        db.ClientBudgets.RemoveRange(db.ClientBudgets);
        await db.SaveChangesAsync();

        Assert.False(await service.AffordabilityCapturedAsync(client.Id));
    }

    [Fact]
    public async Task TheStatementCheck_FeedsTheFlag_AndTheLatestOneCounts()
    {
        var (db, service, client) = await ArrangeAsync();
        db.StatementReviews.Add(new StatementReview { ClientId = client.Id, Flag = StatementFlag.Bad, Summary = "x", CreatedUtc = Today.AddDays(-2) });
        await db.SaveChangesAsync();
        Assert.Equal(ScreeningOutcome.NotGoodToGo, (await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "t", Today))!.Outcome);

        db.StatementReviews.Add(new StatementReview { ClientId = client.Id, Flag = StatementFlag.Good, Summary = "x", CreatedUtc = Today.AddDays(-1) });
        await db.SaveChangesAsync();

        Assert.Equal(ScreeningOutcome.GoodToGo, (await service.EvaluateAsync(client.Id, ScreeningTrigger.Manual, "t", Today))!.Outcome);
    }

    // ----------------------------------------------------------------- employer list

    [Fact]
    public async Task Employer_TypedOnce_IsFoundAgainWhateverTheSpacingOrCase()
    {
        var (db, _, client) = await ArrangeAsync();

        var first = await EmployerDirectory.ResolveAsync(db, client.TenantId, null, "Shoprite Checkers", null);
        await db.SaveChangesAsync();
        var again = await EmployerDirectory.ResolveAsync(db, client.TenantId, null, "  shoprite   checkers ", null);
        var picked = await EmployerDirectory.ResolveAsync(db, client.TenantId, first!.Id, "ignored", null);

        Assert.Equal(first.Id, again!.Id);
        Assert.Equal(first.Id, picked!.Id);
        Assert.Equal(1, await db.Employers.CountAsync());
    }

    [Fact]
    public async Task Employer_ADifferentPayPoint_IsItsOwnEntry_AndAnotherLenderNeverSeesIt()
    {
        var (db, _, client) = await ArrangeAsync();
        var head = await EmployerDirectory.ResolveAsync(db, client.TenantId, null, "Transnet", "Head office");
        await db.SaveChangesAsync();

        var depot = await EmployerDirectory.ResolveAsync(db, client.TenantId, null, "Transnet", "Durban depot");
        var otherLender = await EmployerDirectory.ResolveAsync(db, Guid.NewGuid(), head!.Id, "Transnet", "Head office");
        await db.SaveChangesAsync();

        Assert.NotEqual(head.Id, depot!.Id);
        Assert.NotEqual(head.Id, otherLender!.Id);
        Assert.Null(await EmployerDirectory.ResolveAsync(db, client.TenantId, null, "  ", null));
    }

    // ----------------------------------------------------------------- wording upload

    [Fact]
    public void Docx_ComesOutAsParagraphsOfPlainText()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:r><w:t>1. The </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>Borrower</w:t></w:r><w:r><w:t xml:space="preserve"> agrees.</w:t></w:r></w:p>
              <w:p/><w:p/><w:p/>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>2. In a table</w:t><w:br/><w:t>second line</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """;
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("word/document.xml").Open())
            entry.Write(Encoding.UTF8.GetBytes(xml.Trim()));
        file.Position = 0;

        var (text, error) = DocumentText.Extract("Agreement.DOCX", file);

        Assert.Null(error);
        Assert.Equal("1. The Borrower agrees.\n\n2. In a table\nsecond line", text);
    }

    [Theory]
    [InlineData("terms.pdf", "PDF")]
    [InlineData("terms.doc", "old Word format")]
    [InlineData("terms.docx", "could not be read")]
    public void WhatCannotBeRead_SaysWhy(string name, string expected)
    {
        var (text, error) = DocumentText.Extract(name, new MemoryStream(Encoding.UTF8.GetBytes("not a zip")));

        Assert.Null(text);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void PlainText_IsTidied()
    {
        var (text, _) = DocumentText.Extract("terms.txt", new MemoryStream(Encoding.UTF8.GetBytes("One  \r\n\r\n\r\n\r\nTwo\r\n")));

        Assert.Equal("One\n\nTwo", text);
    }

    // ----------------------------------------------------------------- AI statement verdict

    [Fact]
    public void StatementVerdict_IsReadFromTheModelsJson()
    {
        const string json = """
            {"flag":"Bad","summary":"Salary lands on the 25th and is gone by the 27th.",
             "observations":["3 returned debit orders","Two payday lenders"],
             "average_monthly_income":14250.456,"average_monthly_spend":null}
            """;

        var reading = ClaudeBankStatementAnalyst.Parse(json, "claude-opus-5-5");

        Assert.Equal(StatementFlag.Bad, reading.Flag);
        Assert.Equal(2, reading.Observations.Count);
        Assert.Equal(14250.46m, reading.AverageMonthlyIncome);
        Assert.Null(reading.AverageMonthlySpend);
        Assert.False(reading.Simulated);
    }

    [Fact]
    public async Task WithoutAProvider_HomeAffairsAndTheStatementCheck_SayTheyAreSimulated()
    {
        var life = await new StubLifeStatusVerifier(NullLogger<StubLifeStatusVerifier>.Instance).VerifyAsync(ValidId, "Molefe");
        var (reading, _) = await new StubBankStatementAnalyst(NullLogger<StubBankStatementAnalyst>.Instance)
            .AnalyseAsync(new BankStatementInput(new byte[] { 1 }, "application/pdf", "Test", null));

        Assert.True(life.Simulated);
        Assert.Equal(VerificationResult.Confirmed, life.Result);
        Assert.True(reading!.Simulated);
        Assert.Equal(StatementFlag.Warning, reading.Flag);
    }

    // ----------------------------------------------------------------- arrange

    /// <summary>Netcash's published test ID number: valid, born 15 June 1985.</summary>
    private const string ValidId = "8506155009085";

    private static async Task<(AppDbContext Db, ScreeningService Service, Client Client)> ArrangeAsync(Action<Client>? adjust = null)
    {
        Assert.True(SaIdNumber.IsValid(ValidId));

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"screening-{Guid.NewGuid()}")
            .Options);

        var tenant = new Tenant { Name = "Test Lender (Pty) Ltd" };
        var client = new Client
        {
            TenantId = tenant.Id,
            ClientNumber = "0001",
            FirstName = "Lerato",
            Surname = "Molefe",
            IdNumber = ValidId,
            Status = ClientStatus.Captured
        };
        client.Employment = new ClientEmployment
        {
            ClientId = client.Id, EmployerName = "Shoprite", EmploymentType = "Permanent", EmployedSince = new DateTime(2020, 3, 1)
        };
        client.Financial = new ClientFinancial
        {
            ClientId = client.Id, GrossMonthlyIncome = 20_000m, NetMonthlyIncome = 15_000m,
            TotalMonthlyExpenses = 5_000m, TotalMonthlyDebtRepayments = 1_000m
        };
        client.OtherDetails = new ClientOtherDetails { ClientId = client.Id, DataProcessingConsent = true };
        client.BankAccounts.Add(new ClientBankAccount
        {
            ClientId = client.Id, AccountHolderName = "L Molefe", BranchCode = "250655", AccountNumber = "0000000000", AvsVerified = true
        });
        client.Addresses.Add(new ClientAddress { ClientId = client.Id, MobileNumber = "0820000000" });
        client.Budgets.Add(new ClientBudget { ClientId = client.Id, Description = "Groceries", Amount = 5_000m });
        client.Verifications.Add(new ClientVerification { ClientId = client.Id, Kind = VerificationKind.Life, Result = VerificationResult.Confirmed, Method = "Test" });
        client.Verifications.Add(new ClientVerification { ClientId = client.Id, Kind = VerificationKind.Employment, Result = VerificationResult.Confirmed, Method = "Test" });

        adjust?.Invoke(client);

        db.Tenants.Add(tenant);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var service = new ScreeningService(
            db, new AffordabilityService(Options.Create(new AffordabilityNorms())), NullLogger<ScreeningService>.Instance);
        return (db, service, client);
    }
}
