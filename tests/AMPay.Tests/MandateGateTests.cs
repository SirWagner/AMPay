using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Tests;

/// <summary>No debit mandate before the client has accepted the quote and signed the agreement.</summary>
public class MandateGateTests
{
    [Theory]
    [InlineData(QuoteStatus.Sent, ContractStatus.Sent, "quote")]
    [InlineData(QuoteStatus.Accepted, ContractStatus.Viewed, "agreement")]
    [InlineData(QuoteStatus.Viewed, ContractStatus.Signed, "quote")]
    [InlineData(QuoteStatus.Accepted, ContractStatus.Signed, null)]
    public async Task TheMandateWaitsForBothDocuments(QuoteStatus quote, ContractStatus contract, string? missing)
    {
        await using var db = Db();
        var loan = new Loan { LoanNumber = "LN-1", Status = LoanStatus.Approved };
        db.CreditQuotes.Add(new CreditQuote { LoanId = loan.Id, Status = quote });
        db.LoanContracts.Add(new LoanContract { LoanId = loan.Id, Status = contract });
        await db.SaveChangesAsync();

        var problem = await MandateGate.ProblemForAsync(db, loan);

        if (missing is null) Assert.Null(problem);
        else Assert.Contains(missing, problem);
    }

    [Fact]
    public async Task NoDocumentsAtAll_Blocks()
    {
        await using var db = Db();
        Assert.NotNull(await MandateGate.ProblemForAsync(db, new Loan { LoanNumber = "LN-1", Status = LoanStatus.Approved }));
    }

    [Fact]
    public void AReissuedDocument_MustBeSignedAgain_ButAVoidedOneDoesNotCount()
    {
        var signed = new LoanContract { Issue = 1, Status = ContractStatus.Signed };
        Assert.False(MandateGate.IsSigned(new[] { signed, new LoanContract { Issue = 2, Status = ContractStatus.Sent } }));
        Assert.True(MandateGate.IsSigned(new[] { signed, new LoanContract { Issue = 2, Status = ContractStatus.Voided } }));
    }

    [Fact]
    public async Task ALoanWithAWorkingMandate_CannotGetAnother()
    {
        await using var db = Db();
        var m = new DebiCheckMandate { Status = MandateStatus.AwaitingDebtorAuthentication };
        db.Mandates.Add(m);
        await db.SaveChangesAsync();

        Assert.Contains("already", await MandateGate.ProblemForAsync(db, new Loan { Status = LoanStatus.Approved, MandateId = m.Id }));
    }

    private static AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"gate-{Guid.NewGuid()}").Options);
}
