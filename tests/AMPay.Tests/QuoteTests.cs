using System.Text.RegularExpressions;
using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AMPay.Tests;

public class QuoteTests
{
    private static readonly int[] AllConsents = { 0, 1, 2 };

    // ----------------------------------------------------------------- issuing

    [Fact]
    public async Task Issue_FreezesTheLoanAndIsBindingForFiveBusinessDays()
    {
        var (_, _, quotes, loan) = await ArrangeAsync();

        var quote = await quotes.IssueAsync(loan.Id, "user-1");

        Assert.Equal($"{loan.LoanNumber}/Q1", quote.Reference);
        Assert.Equal(QuoteStatus.Issued, quote.Status);
        Assert.Equal(ContractSnapshot.HashOf(quote.SnapshotJson), quote.SnapshotHash);
        Assert.Equal(QuoteService.ReadSnapshot(quote).TermsFingerprint(), quote.TermsHash);
        Assert.Equal(loan.TotalRepayable, QuoteService.ReadSnapshot(quote).Quote.TotalRepayable);
        Assert.Equal(QuoteService.ValidUntil(quote.CreatedUtc), quote.ValidUntilUtc);
    }

    [Fact]
    public void ValidUntil_SkipsWeekendsAndPublicHolidays()
    {
        // Thursday 24 Sep 2026 is Heritage Day. Issued Wednesday 23 Sep at 10:00 SAST:
        // Fri 25, Mon 28, Tue 29, Wed 30, Thu 1 Oct - binding to the end of Thursday 1 October.
        var issued = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 1, 21, 59, 59), QuoteService.ValidUntil(issued));
    }

    [Fact]
    public async Task Issue_RefusesALoanThatIsNotApproved()
    {
        var (_, _, quotes, loan) = await ArrangeAsync(LoanStatus.PendingApproval);

        await Assert.ThrowsAsync<ContractException>(() => quotes.IssueAsync(loan.Id, "user-1"));
    }

    [Fact]
    public async Task Issue_RefusesABusinessWithoutAnNcrNumber()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        (await db.Tenants.SingleAsync()).NcrNumber = null;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ContractException>(() => quotes.IssueAsync(loan.Id, "user-1"));
        Assert.Contains("NCR", ex.Message);
    }

    [Fact]
    public async Task IssuingAgain_VoidsTheEarlierQuote()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();

        var first = await quotes.IssueAsync(loan.Id, "user-1");
        var second = await quotes.IssueAsync(loan.Id, "user-1");

        Assert.Equal($"{loan.LoanNumber}/Q2", second.Reference);
        Assert.Equal(QuoteStatus.Voided, (await db.CreditQuotes.SingleAsync(q => q.Id == first.Id)).Status);
    }

    // ----------------------------------------------------------------- the client's answer

    [Fact]
    public async Task AcceptOnline_WithAllConsentsAndTheRightPin()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);

        await quotes.SendPinAsync(quote);
        await quotes.AcceptOnlineAsync(quote, await LastPinAsync(db), AllConsents, "10.0.0.1", "test");

        Assert.Equal(QuoteStatus.Accepted, quote.Status);
        Assert.Equal(QuoteAcceptanceMethod.OnlineOtp, quote.AcceptanceMethod);
        Assert.Equal("0821234567", quote.AcceptedMobile);
        Assert.Contains("true and complete", quote.ConsentsJson);
        Assert.Null(quote.OtpHash);
    }

    [Fact]
    public async Task AcceptOnline_RefusesWithoutEveryConsent()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);
        await quotes.SendPinAsync(quote);

        await Assert.ThrowsAsync<ContractException>(() =>
            quotes.AcceptOnlineAsync(quote, LastPinAsync(db).Result, new[] { 0, 2 }, null, null));

        Assert.Equal(QuoteStatus.Sent, quote.Status);
    }

    [Fact]
    public async Task ThreeWrongPins_BurnThePin()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);
        await quotes.SendPinAsync(quote);
        var right = await LastPinAsync(db);
        var wrong = right == "000000" ? "111111" : "000000";

        for (var i = 0; i < QuoteService.MaxPinAttempts; i++)
            await Assert.ThrowsAsync<ContractException>(() => quotes.AcceptOnlineAsync(quote, wrong, AllConsents, null, null));

        Assert.Null(quote.OtpHash);
        await Assert.ThrowsAsync<ContractException>(() => quotes.AcceptOnlineAsync(quote, right, AllConsents, null, null));
    }

    [Fact]
    public async Task AnExpiredPin_IsRefused()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);
        await quotes.SendPinAsync(quote);
        quote.OtpExpiresUtc = DateTime.UtcNow.AddSeconds(-1);

        var ex = await Assert.ThrowsAsync<ContractException>(() =>
            quotes.AcceptOnlineAsync(quote, LastPinAsync(db).Result, AllConsents, null, null));
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task Pins_AreLimitedToThreeAnHour()
    {
        var (_, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);

        for (var i = 0; i < QuoteService.MaxPinsPerHour; i++)
        {
            await quotes.SendPinAsync(quote);
            quote.OtpSentUtc = DateTime.UtcNow.AddMinutes(-2); // past the one-minute resend wait
        }

        var ex = await Assert.ThrowsAsync<ContractException>(() => quotes.SendPinAsync(quote));
        Assert.Contains("last hour", ex.Message);
    }

    [Fact]
    public async Task Decline_RecordsTheReason()
    {
        var (_, _, quotes, loan) = await ArrangeAsync();
        var quote = await SentQuoteAsync(quotes, loan);

        await Assert.ThrowsAsync<ContractException>(() => quotes.DeclineAsync(quote, "Other", null));
        await quotes.DeclineAsync(quote, "The instalment is too high", null);

        Assert.Equal(QuoteStatus.Declined, quote.Status);
        Assert.Equal("The instalment is too high", quote.DeclineReason);
        await Assert.ThrowsAsync<ContractException>(() => quotes.SendPinAsync(quote));
    }

    [Fact]
    public async Task AQuotePastItsValidity_ExpiresWhenOpened()
    {
        var (db, _, quotes, loan) = await ArrangeAsync();
        var quote = await quotes.IssueAsync(loan.Id, "user-1");
        var link = (await quotes.SendAsync(quote.Id, true, false, "https://test", "user-1")).Link;
        quote.ValidUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var opened = await quotes.FindByTokenAsync(link.Split('/').Last());

        Assert.NotNull(opened);
        Assert.Equal(QuoteStatus.Expired, opened!.Status);
        await Assert.ThrowsAsync<ContractException>(() => quotes.SendPinAsync(opened));
    }

    [Fact]
    public async Task AWrongLink_FindsNothing()
    {
        var (_, _, quotes, loan) = await ArrangeAsync();
        await SentQuoteAsync(quotes, loan);

        Assert.Null(await quotes.FindByTokenAsync("not-a-real-token"));
    }

    // ----------------------------------------------------------------- the gate on the agreement

    [Fact]
    public async Task TheAgreement_NeedsAnAcceptedQuote()
    {
        var (_, contracts, quotes, loan) = await ArrangeAsync();
        await quotes.IssueAsync(loan.Id, "user-1");

        var ex = await Assert.ThrowsAsync<ContractException>(() => contracts.IssueAsync(loan.Id, "user-1"));
        Assert.Contains("accepted a quote", ex.Message);
    }

    [Fact]
    public async Task TheAgreement_CarriesTheAcceptedTermsAndQuoteDate()
    {
        var (_, contracts, quotes, loan) = await ArrangeAsync();
        var quote = await quotes.IssueAsync(loan.Id, "user-1");
        await quotes.RecordInBranchAsync(quote.Id, AllConsents, "user-1");

        var contract = await contracts.IssueAsync(loan.Id, "user-1");
        var s = ContractService.ReadSnapshot(contract);

        Assert.Equal(quote.TermsHash, s.TermsFingerprint());
        Assert.Equal(QuoteService.ReadSnapshot(quote).Quote.QuoteDate, s.Quote.QuoteDate);
    }

    [Fact]
    public async Task TheAgreement_IsRefusedWhenTheLoanChangedAfterAcceptance()
    {
        var (db, contracts, quotes, loan) = await ArrangeAsync();
        var quote = await quotes.IssueAsync(loan.Id, "user-1");
        await quotes.RecordInBranchAsync(quote.Id, AllConsents, "user-1");

        var line = await db.LoanScheduleEntries.FirstAsync(e => e.LoanId == loan.Id);
        line.InstalmentTotal += 1m;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ContractException>(() => contracts.IssueAsync(loan.Id, "user-1"));
        Assert.Contains("changed", ex.Message);
    }

    [Fact]
    public async Task ANewQuote_VoidsAnUnsignedAgreement()
    {
        var (db, contracts, quotes, loan) = await ArrangeAsync();
        var quote = await quotes.IssueAsync(loan.Id, "user-1");
        await quotes.RecordInBranchAsync(quote.Id, AllConsents, "user-1");
        var contract = await contracts.IssueAsync(loan.Id, "user-1");

        await quotes.IssueAsync(loan.Id, "user-1");

        Assert.Equal(ContractStatus.Voided, (await db.LoanContracts.SingleAsync(c => c.Id == contract.Id)).Status);
    }

    // ----------------------------------------------------------------- helpers

    private static async Task<CreditQuote> SentQuoteAsync(QuoteService quotes, Loan loan)
    {
        var quote = await quotes.IssueAsync(loan.Id, "user-1");
        await quotes.SendAsync(quote.Id, true, false, "https://test", "user-1");
        return quote;
    }

    /// <summary>The stub outbox keeps the full body, so the PIN can be read back as staff would in a test system.</summary>
    private static async Task<string> LastPinAsync(AppDbContext db)
    {
        var body = await db.OutboundMessages
            .Where(m => m.Context != null && m.Context.StartsWith("Quote PIN"))
            .OrderByDescending(m => m.CreatedUtc)
            .Select(m => m.Body)
            .FirstAsync();
        return Regex.Match(body, @" is (\d{6})\.").Groups[1].Value;
    }

    private static async Task<(AppDbContext Db, ContractService Contracts, QuoteService Quotes, Loan Loan)> ArrangeAsync(
        LoanStatus status = LoanStatus.Approved)
    {
        var (db, contracts, loan) = await ContractTests.ArrangeAsync(status, acceptQuote: false);
        IMessageSender messages = new OutboxMessageSender(db, NullLogger<OutboxMessageSender>.Instance);
        return (db, contracts, new QuoteService(db, contracts, messages, NullLogger<QuoteService>.Instance), loan);
    }
}
