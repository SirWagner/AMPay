using System.Text;
using System.Text.RegularExpressions;
using AMPay.Domain.Contracts;
using AMPay.Domain.Credit;
using AMPay.Domain.Documents;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Credit;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

public class ContractTests
{
    private const string IdNumber = "8501015800085";
    private const string Mobile = "0821234567";

    // ----------------------------------------------------------------- issuing

    [Fact]
    public async Task Issue_FreezesTheLoanIntoAPackWhoseFingerprintMatches()
    {
        var (db, svc, loan) = await ArrangeAsync();

        var contract = await svc.IssueAsync(loan.Id, "user-1");

        Assert.Equal($"{loan.LoanNumber}/1", contract.Reference);
        Assert.Equal(ContractStatus.Issued, contract.Status);
        Assert.Equal(ContractSnapshot.HashOf(contract.SnapshotJson), contract.SnapshotHash);

        var s = ContractService.ReadSnapshot(contract);
        Assert.Equal(loan.Principal, s.Quote.LoanAmount);
        Assert.Equal(loan.TotalRepayable, s.Quote.TotalRepayable);
        Assert.Equal(loan.TotalCostOfCredit, s.Quote.TotalCostOfCredit);
        Assert.Equal(loan.NumberOfInstalments, s.Schedule.Count);
        Assert.Equal(IdNumber, s.Borrower.IdNumber);
        Assert.Equal("******7890", s.Mandate.MaskedAccountNumber);
        Assert.NotNull(s.Budget);
        Assert.NotNull(s.CreditLife);

        // Placeholder wording is seeded on first issue and is never approved.
        Assert.False(contract.TemplatesApproved);
        Assert.Equal(4, s.Terms.Count);
        Assert.Equal(4, await db.ContractTemplates.CountAsync());
    }

    [Fact]
    public async Task Issue_RefusesALoanThatIsNotApproved()
    {
        var (_, svc, loan) = await ArrangeAsync(LoanStatus.PendingApproval);

        await Assert.ThrowsAsync<ContractException>(() => svc.IssueAsync(loan.Id, "user-1"));
    }

    [Fact]
    public async Task IssuingAgain_VoidsTheOpenPack_SoOnlyOneCanBeSigned()
    {
        var (db, svc, loan) = await ArrangeAsync();

        var first = await svc.IssueAsync(loan.Id, "user-1");
        var second = await svc.IssueAsync(loan.Id, "user-1");

        Assert.Equal($"{loan.LoanNumber}/2", second.Reference);
        var reloaded = await db.LoanContracts.SingleAsync(c => c.Id == first.Id);
        Assert.Equal(ContractStatus.Voided, reloaded.Status);
        Assert.Null(reloaded.AccessTokenHash);
    }

    [Fact]
    public async Task APackEditedAfterIssue_IsRefused()
    {
        var (_, svc, loan) = await ArrangeAsync();
        var contract = await svc.IssueAsync(loan.Id, "user-1");

        contract.SnapshotJson = contract.SnapshotJson.Replace("Mokoena", "Mokoema");

        Assert.Throws<InvalidOperationException>(() => ContractService.ReadSnapshot(contract));
    }

    [Fact]
    public async Task CreditLifeSection_IsLeftOut_WhenTheLoanCarriesNone()
    {
        var (_, svc, loan) = await ArrangeAsync(creditLifeRate: 0m);

        var s = ContractService.ReadSnapshot(await svc.IssueAsync(loan.Id, "user-1"));

        Assert.Null(s.CreditLife);
        Assert.DoesNotContain(s.Terms, t => t.Kind == nameof(ContractTemplateKind.CreditLifeDisclosure));
    }

    // ----------------------------------------------------------------- sending

    [Fact]
    public async Task Send_StoresOnlyAHashOfTheLink_AndRecordsBothMessages()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await svc.IssueAsync(loan.Id, "user-1");

        var outcome = await svc.SendAsync(contract.Id, bySms: true, byEmail: true, "https://apply.test", "user-1");
        var token = TokenFrom(outcome.Link);

        Assert.StartsWith("https://apply.test/sign/", outcome.Link);
        Assert.True(outcome.Stubbed);
        Assert.NotEqual(token, contract.AccessTokenHash);
        Assert.Equal(ContractStatus.Sent, contract.Status);
        Assert.Equal(contract.Id, (await svc.FindByTokenAsync(token))!.Id);

        var messages = await db.OutboundMessages.ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Channel == MessageChannel.Sms && m.To == Mobile);
        Assert.Contains(messages, m => m.Channel == MessageChannel.Email);
    }

    [Fact]
    public async Task Send_RefusesDraftWording_OnceRealMessagingIsConnected()
    {
        var (_, svc, loan) = await ArrangeAsync(sender: new LiveSender());
        var contract = await svc.IssueAsync(loan.Id, "user-1");

        var ex = await Assert.ThrowsAsync<ContractException>(() =>
            svc.SendAsync(contract.Id, true, true, "https://apply.test", "user-1"));
        Assert.Contains("draft wording", ex.Message);
    }

    [Fact]
    public async Task AWrongOrUnknownLink_FindsNothing()
    {
        var (_, svc, _) = await ArrangeAsync();

        Assert.Null(await svc.FindByTokenAsync("not-a-real-token"));
        Assert.Null(await svc.FindByTokenAsync(null));
    }

    [Fact]
    public async Task VoidingAPack_KillsItsLink()
    {
        var (_, svc, loan) = await ArrangeAsync();
        var contract = await svc.IssueAsync(loan.Id, "user-1");
        var token = TokenFrom((await svc.SendAsync(contract.Id, true, false, "https://apply.test", "u")).Link);

        await svc.VoidAsync(contract.Id, "Client asked for a shorter term.", "user-1");

        Assert.Null(await svc.FindByTokenAsync(token));
    }

    // ----------------------------------------------------------------- signing online

    [Fact]
    public async Task SigningWithTheRightPinAndIdNumber_RecordsTheSignature()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);

        await svc.SendPinAsync(contract);
        var pin = await LatestPinAsync(db);

        await svc.SignOnlineAsync(contract, pin, "Thabo Mokoena", IdNumber, accepted: true, "196.25.1.1", "test-agent");

        Assert.Equal(ContractStatus.Signed, contract.Status);
        Assert.Equal(SignatureMethod.OnlineOtp, contract.SignatureMethod);
        Assert.Equal("Thabo Mokoena", contract.SignedName);
        Assert.Equal("196.25.1.1", contract.SignedIp);
        Assert.Equal(Mobile, contract.SignedMobile);
        Assert.Null(contract.OtpHash);
    }

    [Fact]
    public async Task TheWrongIdNumber_IsRefused_EvenWithTheRightPin()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);
        await svc.SendPinAsync(contract);
        var pin = await LatestPinAsync(db);

        var ex = await Assert.ThrowsAsync<ContractException>(() =>
            svc.SignOnlineAsync(contract, pin, "Somebody Else", "9001015800087", true, null, null));

        Assert.Equal("The PIN or ID number is wrong.", ex.Message);
        Assert.Equal(1, contract.OtpFailedAttempts);
        Assert.NotEqual(ContractStatus.Signed, contract.Status);
    }

    [Fact]
    public async Task FiveWrongPins_BurnThePin()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);
        await svc.SendPinAsync(contract);
        var pin = await LatestPinAsync(db);
        var wrong = pin == "000000" ? "111111" : "000000";

        for (var i = 0; i < ContractService.MaxPinAttempts; i++)
            await Assert.ThrowsAsync<ContractException>(() =>
                svc.SignOnlineAsync(contract, wrong, "Thabo Mokoena", IdNumber, true, null, null));

        // The right PIN no longer works either: a new one must be requested.
        await Assert.ThrowsAsync<ContractException>(() =>
            svc.SignOnlineAsync(contract, pin, "Thabo Mokoena", IdNumber, true, null, null));
        Assert.Null(contract.OtpHash);
    }

    [Fact]
    public async Task AskingForAnotherPinWithinAMinute_IsRefused()
    {
        var (_, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);

        await svc.SendPinAsync(contract);

        await Assert.ThrowsAsync<ContractException>(() => svc.SendPinAsync(contract));
    }

    [Fact]
    public async Task ThePinIsMaskedInTheAuditCopy()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);

        await svc.SendPinAsync(contract);

        // The stub keeps the full body (testing needs it); the audit copy a real sender keeps does not.
        var captured = Assert.IsType<CapturingSender>(svc.GetType()
            .GetField("_messages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(svc));
        var pinMessage = captured.Sent.Last();
        var pin = PinIn(pinMessage.Body);
        Assert.Equal(6, pin.Length);
        Assert.DoesNotContain(pin, pinMessage.AuditBody!);
        Assert.Contains("******", pinMessage.AuditBody!);
    }

    // ----------------------------------------------------------------- signing on paper

    [Fact]
    public async Task UploadingTheSignedCopy_FilesItAndMarksThePackSigned()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await svc.IssueAsync(loan.Id, "user-1");

        await svc.RecordSignedCopyAsync(contract.Id, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 signed")),
            "signed.pdf", "application/pdf", "user-2");

        var reloaded = await db.LoanContracts.SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Signed, reloaded.Status);
        Assert.Equal(SignatureMethod.UploadedSignedCopy, reloaded.SignatureMethod);
        Assert.Equal("user-2", reloaded.SignedRecordedByUserId);

        var doc = await db.ClientDocuments.SingleAsync(d => d.Id == reloaded.SignedCopyDocumentId);
        Assert.Equal(DocumentType.SignedAgreement, doc.DocumentType);
    }

    [Fact]
    public async Task ASignedPackOnADisbursedLoan_CannotBeVoided()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await svc.IssueAsync(loan.Id, "user-1");
        await svc.RecordSignedCopyAsync(contract.Id, new MemoryStream(new byte[] { 1, 2, 3 }), "s.pdf", null, "u");

        loan.Status = LoanStatus.Disbursed;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ContractException>(() => svc.VoidAsync(contract.Id, "Mistake", "u"));
    }

    // ----------------------------------------------------------------- rendering

    [Fact]
    public async Task ThePdf_RendersForADraftAndForASignedPack()
    {
        var (db, svc, loan) = await ArrangeAsync();
        var contract = await SentContractAsync(svc, loan);

        var draft = svc.RenderPdf(contract);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(draft, 0, 4));

        await svc.SendPinAsync(contract);
        await svc.SignOnlineAsync(contract, await LatestPinAsync(db), "Thabo Mokoena", IdNumber, true, "1.2.3.4", null);

        var signed = svc.RenderPublicPdf(contract);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(signed, 0, 4));
        Assert.True(signed.Length > 5_000);
    }

    [Fact]
    public async Task ThePublicCopy_MasksTheIdNumber()
    {
        var (_, svc, loan) = await ArrangeAsync();
        var s = ContractService.ReadSnapshot(await svc.IssueAsync(loan.Id, "user-1"));

        var pub = s.ForPublicView();

        Assert.Equal("850101*******", pub.Borrower.IdNumber);
        Assert.Equal(IdNumber, s.Borrower.IdNumber);
    }

    [Fact]
    public void TemplateParagraphs_SplitOnBlankLinesAndJoinWrappedLines()
    {
        var p = ContractFormat.Paragraphs("1. First line\nwraps here.\r\n\r\n2. Second.\n\n\n");

        Assert.Equal(new[] { "1. First line wraps here.", "2. Second." }, p);
    }

    [Fact]
    public void DefaultWording_IsMarkedAsDraft_ForEverySection()
    {
        var templates = ContractTemplateDefaults.For(Guid.NewGuid());

        Assert.Equal(Enum.GetValues<ContractTemplateKind>().Length, templates.Count);
        Assert.All(templates, t =>
        {
            Assert.False(t.IsApproved);
            Assert.StartsWith("DRAFT WORDING", t.Body);
        });
    }

    // ----------------------------------------------------------------- fixture

    private static async Task<LoanContract> SentContractAsync(ContractService svc, Loan loan)
    {
        var contract = await svc.IssueAsync(loan.Id, "user-1");
        await svc.SendAsync(contract.Id, true, true, "https://apply.test", "user-1");
        return contract;
    }

    private static string TokenFrom(string link) => link[(link.LastIndexOf('/') + 1)..];

    private static async Task<string> LatestPinAsync(AppDbContext db)
    {
        var sms = await db.OutboundMessages
            .Where(m => m.Context != null && m.Context.StartsWith("Signing PIN"))
            .OrderByDescending(m => m.CreatedUtc)
            .FirstAsync();
        return PinIn(sms.Body);
    }

    // Anchored on " is ": the agreement reference (LN-000001) also contains six digits.
    private static string PinIn(string body) => Regex.Match(body, @" is (\d{6})\.").Groups[1].Value;

    private static async Task<(AppDbContext Db, ContractService Svc, Loan Loan)> ArrangeAsync(
        LoanStatus status = LoanStatus.Approved,
        decimal creditLifeRate = 0.0045m,
        IMessageSender? sender = default)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"contracts-{Guid.NewGuid()}")
            .Options);

        var tenant = new Tenant
        {
            Name = "Albatross Money (Pty) Ltd",
            TradingName = "Albatross Money",
            NcrNumber = "NCRCP9166",
            ContactNumber = "011 894 1087"
        };

        var package = new CreditPackage
        {
            TenantId = tenant.Id,
            Tier = CreditTier.Regular,
            Name = "Regular",
            MonthlyInterestRate = 0.05m,
            MonthlyServiceFee = 60m,
            InitiationFeeRate = 0.15m,
            CreditLifeRate = creditLifeRate,
            MinLoanAmount = 500m,
            MaxLoanAmount = 8_000m,
            MinTermMonths = 1,
            MaxTermMonths = 6
        };

        var client = new Client
        {
            TenantId = tenant.Id,
            ClientNumber = "CL-000001",
            FirstName = "Thabo",
            Surname = "Mokoena",
            IdNumber = IdNumber,
            Status = ClientStatus.Onboarded,
            Employment = new ClientEmployment { EmployerName = "Example Mining" },
            Financial = new ClientFinancial
            {
                GrossMonthlyIncome = 18_000m,
                NetMonthlyIncome = 14_500m,
                TotalMonthlyExpenses = 6_000m,
                TotalMonthlyDebtRepayments = 1_200m
            }
        };
        client.Addresses.Add(new ClientAddress
        {
            AddressType = AddressType.Physical,
            Line1 = "12 Example Street",
            City = "Germiston",
            MobileNumber = Mobile,
            EmailAddress = "thabo@example.test"
        });
        client.BankAccounts.Add(new ClientBankAccount
        {
            AccountHolderName = "T Mokoena",
            BankName = "Capitec",
            BranchCode = "470010",
            AccountNumber = "1234567890",
            IsPrimary = true
        });
        client.Budgets.Add(new ClientBudget { Kind = BudgetLineKind.Expense, Description = "Rent", Amount = 3_500m });
        client.Budgets.Add(new ClientBudget { Kind = BudgetLineKind.Expense, Description = "Groceries", Amount = 2_500m });

        var quote = new LoanPricingService(Options.Create(new NcaCreditLimits()))
            .Quote(new LoanQuoteRequest(5_000m, 3, new DateTime(2026, 10, 25)), package);

        var loan = new Loan
        {
            TenantId = tenant.Id,
            ClientId = client.Id,
            CreditPackageId = package.Id,
            LoanNumber = "LN-000001",
            Status = status
        };
        var schedule = LoanOrigination.ApplyQuote(loan, quote);

        db.Tenants.Add(tenant);
        db.CreditPackages.Add(package);
        db.Clients.Add(client);
        db.Loans.Add(loan);
        db.LoanScheduleEntries.AddRange(schedule);
        await db.SaveChangesAsync();

        // Default: the outbox stub, wrapped so a test can see exactly what was asked of it.
        IMessageSender messages = sender ?? new CapturingSender(db);

        var svc = new ContractService(
            db,
            new AffordabilityService(Options.Create(new AffordabilityNorms())),
            new MigraDocContractRenderer(),
            messages,
            new MemoryDocumentStore(),
            NullLogger<ContractService>.Instance);

        return (db, svc, loan);
    }

    /// <summary>A sender that claims to be live, to test the gates that apply once real messaging is on.</summary>
    private sealed class LiveSender : IMessageSender
    {
        public bool IsStubbed => false;
        public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken ct = default) =>
            Task.FromResult(new SendResult(true, MessageStatus.Sent));
    }

    /// <summary>Records what was asked of it and writes the outbox row like the stub does.</summary>
    private sealed class CapturingSender : IMessageSender
    {
        private readonly OutboxMessageSender _inner;
        public CapturingSender(AppDbContext db) => _inner = new OutboxMessageSender(db, NullLogger<OutboxMessageSender>.Instance);
        public List<OutgoingMessage> Sent { get; } = new();
        public bool IsStubbed => true;

        public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return _inner.SendAsync(message, ct);
        }
    }

    private sealed class MemoryDocumentStore : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<StoredDocument> SaveAsync(Guid tenantId, Guid clientId, string fileName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var path = $"{tenantId:N}/{clientId:N}/{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
            _files[path] = ms.ToArray();
            return new StoredDocument(path, ContractSnapshot.HashOf(Convert.ToBase64String(ms.ToArray())), ms.Length);
        }

        public Task<Stream?> OpenAsync(string storagePath, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(_files.TryGetValue(storagePath, out var b) ? new MemoryStream(b) : null);

        public Task DeleteAsync(string storagePath, CancellationToken ct = default)
        {
            _files.Remove(storagePath);
            return Task.CompletedTask;
        }
    }
}
