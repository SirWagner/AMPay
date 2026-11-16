using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Messaging;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AMPay.Tests;

/// <summary>The client's own account on the portal: what they see, and how it reaches them.</summary>
public class ClientPortalAccountTests
{
    private static readonly DateTime Today = new(2026, 11, 16);
    private static readonly DateTime Now = new(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
    private const string IdNumber = "8501015800088"; // fictional

    // ----------------------------------------------------------------- the statement

    [Fact]
    public void TheStatement_ShowsArrears_TheNextInstalment_AndEveryDebit_FailedOnesIncluded()
    {
        var (client, loan) = ClientWithLoan(LoanStatus.Disbursed,
            new DateTime(2026, 9, 25), new DateTime(2026, 10, 25), new DateTime(2026, 11, 25), new DateTime(2026, 12, 25));
        var s = loan.Schedule.OrderBy(e => e.InstalmentNumber).ToList();

        // September paid by debit order; October's debit returned unpaid; November not yet due.
        s[0].PaidAmount = 1_000m;
        loan.Collections.Add(Collection(loan, s[0], CollectionStatus.Successful, new DateTime(2026, 9, 25)));
        loan.Collections.Add(Collection(loan, s[1], CollectionStatus.Failed, new DateTime(2026, 10, 25), "02"));

        var st = ClientStatementBuilder.Build(client, new[] { loan }, Today, Now).Loans.Single();

        Assert.Equal("Active", st.Status);
        Assert.Equal(1_000m, st.PaidToDate);
        Assert.Equal(3_000m, st.RemainingInstalments);
        Assert.Equal(1_000m, st.ArrearsAmount);
        Assert.Equal(22, st.DaysPastDue);
        Assert.Equal(new DateTime(2026, 11, 25), st.NextDueDate);
        Assert.Equal(1_000m, st.NextDueAmount);
        Assert.Equal(1, st.InstalmentsPaid);
        Assert.Equal(new[] { "Paid", "Overdue", "Upcoming", "Upcoming" }, st.Schedule.Select(e => e.Status));

        var unpaid = st.Payments.Single(p => p.Status == "Unpaid");
        Assert.Equal("Insufficient funds (bank code 02)", unpaid.Reason);
        Assert.Equal(2, unpaid.InstalmentNumber);
        Assert.Equal("Debit order (DebiCheck)", unpaid.Method);
    }

    [Fact]
    public void EveryCostElement_ReachesTheClient()
    {
        var (client, loan) = ClientWithLoan(LoanStatus.Disbursed, new DateTime(2026, 12, 25));
        loan.InitiationFee = 165m; loan.TotalInterest = 300m; loan.TotalServiceFees = 69m; loan.TotalVat = 35.1m;
        loan.TotalCreditLife = 12m; loan.FuneralCover = 50m; loan.TotalCostOfCredit = 631.1m; loan.TotalRepayable = 1_631.1m;

        var st = ClientStatementBuilder.Build(client, new[] { loan }, Today, Now).Loans.Single();

        Assert.Equal((165m, 300m, 69m, 35.1m, 12m, 50m, 631.1m, 1_631.1m),
            (st.InitiationFee, st.TotalInterest, st.TotalServiceFees, st.TotalVat, st.TotalCreditLife, st.FuneralCover, st.TotalCostOfCredit, st.TotalRepayable));
    }

    [Fact]
    public void ADraftLoan_IsTheLendersWorking_AndIsNotShown_ButADeclinedOneIs()
    {
        var (client, draft) = ClientWithLoan(LoanStatus.Draft, new DateTime(2026, 12, 25));
        var (_, declined) = ClientWithLoan(LoanStatus.Declined, new DateTime(2026, 12, 25));

        var st = ClientStatementBuilder.Build(client, new[] { draft, declined }, Today, Now);

        var only = Assert.Single(st.Loans);
        Assert.Equal("Declined", only.Status);
        Assert.Equal("Closed", only.Stage);
        Assert.Equal(0m, only.ArrearsAmount);   // nothing is owed on a loan never paid out
    }

    [Fact]
    public void TheIdNumber_IsSentOnlyAsAFingerprint_TiedToTheLender()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(ClientStatementBuilder.IdNumberHash(a, IdNumber), ClientStatementBuilder.IdNumberHash(a, " 850101 5800 088 "));
        Assert.NotEqual(ClientStatementBuilder.IdNumberHash(a, IdNumber), ClientStatementBuilder.IdNumberHash(b, IdNumber));
        Assert.DoesNotContain(IdNumber, ClientStatementBuilder.IdNumberHash(a, IdNumber));
        Assert.Equal(64, ClientStatementBuilder.IdNumberHash(a, IdNumber).Length);
    }

    [Fact]
    public void TheCellNumber_ComesFromThePhysicalAddressFirst_Normalised()
    {
        var client = new Client { FirstName = "Thandi", Surname = "Mokoena", IdNumber = IdNumber };
        client.Addresses.Add(new ClientAddress { AddressType = AddressType.Postal, MobileNumber = "083 000 0000" });
        client.Addresses.Add(new ClientAddress { AddressType = AddressType.Physical, MobileNumber = "082 123 4567" });

        Assert.Equal("27821234567", ClientStatementBuilder.MobileOf(client));
    }

    // ----------------------------------------------------------------- reaching the portal

    [Fact]
    public async Task OpeningTheAccount_SendsTheStatement_AndTheLinkBySms()
    {
        var (db, portal, publisher, client) = await ArrangeAsync();

        var error = await publisher.GrantAccessAsync(client, "staff@lender");

        Assert.Null(error);
        Assert.NotNull(client.PortalAccessUtc);
        var sent = Assert.Single(portal.Sent);
        Assert.Equal("27821234567", sent.Mobile);
        Assert.Equal(ClientStatementBuilder.IdNumberHash(client.TenantId, IdNumber), sent.IdNumberHash);
        Assert.Single(sent.Loans);

        var sms = await db.OutboundMessages.SingleAsync();
        Assert.Equal(MessageChannel.Sms, sms.Channel);
        Assert.Contains("https://portal.test/a/GRN4KX/account", sms.Body);
        Assert.Contains(await db.ClientNotes.ToListAsync(), n => n.Category == "Client portal");
    }

    [Fact]
    public async Task AnUnchangedAccount_IsNotSentAgain_ButAPaymentIs()
    {
        var (db, portal, publisher, client) = await ArrangeAsync();
        await publisher.GrantAccessAsync(client, null);

        await publisher.PublishAsync(client);
        Assert.Single(portal.Sent);

        var loan = await db.Loans.Include(l => l.Schedule).SingleAsync();
        db.LoanCollections.Add(new LoanCollection
        {
            TenantId = client.TenantId, LoanId = loan.Id, Channel = CollectionChannel.Manual, Amount = 500m,
            ActionDate = Today, Status = CollectionStatus.Successful, ReceivedUtc = Now
        });
        await db.SaveChangesAsync();

        await publisher.PublishAsync(client);
        Assert.Equal(2, portal.Sent.Count);
        Assert.Equal(500m, portal.Sent[^1].Loans.Single().PaidToDate);
    }

    [Fact]
    public async Task WithoutASelfServiceLink_OrACellNumber_AccessIsNotOpened()
    {
        var (db, portal, publisher, client) = await ArrangeAsync();

        var tenant = await db.Tenants.SingleAsync();
        tenant.SelfServiceCode = null;
        await db.SaveChangesAsync();
        Assert.Contains("self-service link", await publisher.GrantAccessAsync(client, null));

        tenant.SelfServiceCode = "GRN4KX";
        client.Addresses.Single().MobileNumber = null;
        await db.SaveChangesAsync();
        Assert.Contains("cell number", await publisher.GrantAccessAsync(client, null));

        Assert.Null(client.PortalAccessUtc);
        Assert.Empty(portal.Sent);
        Assert.Empty(await db.OutboundMessages.ToListAsync());
    }

    [Fact]
    public async Task WithdrawingAccess_RemovesTheAccountFromThePortal()
    {
        var (_, portal, publisher, client) = await ArrangeAsync();
        await publisher.GrantAccessAsync(client, null);

        Assert.Null(await publisher.RevokeAccessAsync(client, null));

        Assert.Equal(client.Id, Assert.Single(portal.Deleted));
        Assert.Null(client.PortalAccessUtc);
        Assert.Null(client.PortalStatementHash);

        // Without access, changes are no longer sent.
        await publisher.PublishAsync(client, force: true);
        Assert.Single(portal.Sent);
    }

    [Fact]
    public async Task SavingAPayment_QueuesTheLoanForThePortal()
    {
        var queue = new ClientPortalQueue();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"portal-track-{Guid.NewGuid()}")
            .AddInterceptors(new ClientPortalChangeTracker(queue))
            .Options;
        await using var db = new AppDbContext(options);

        var loanId = Guid.NewGuid();
        db.LoanCollections.Add(new LoanCollection { TenantId = Guid.NewGuid(), LoanId = loanId, Amount = 100m, ActionDate = Today });
        db.ClientNotes.Add(new ClientNote { ClientId = Guid.NewGuid(), Body = "not something the client sees" });
        await db.SaveChangesAsync();

        Assert.True(queue.Reader.TryRead(out var item));
        Assert.Equal(loanId, item.LoanId);
        Assert.False(queue.Reader.TryRead(out _));
    }

    // ----------------------------------------------------------------- helpers

    private static (Client, Loan) ClientWithLoan(LoanStatus status, params DateTime[] due)
    {
        var tenantId = Guid.NewGuid();
        var client = new Client { TenantId = tenantId, ClientNumber = "C-0001", FirstName = "Thandi", Surname = "Mokoena", IdNumber = IdNumber };
        client.Addresses.Add(new ClientAddress { ClientId = client.Id, AddressType = AddressType.Physical, MobileNumber = "082 123 4567" });

        var loan = new Loan
        {
            TenantId = tenantId, ClientId = client.Id, LoanNumber = $"LN-{Random.Shared.Next(100000, 999999)}", Status = status,
            Principal = 800m * due.Length, NumberOfInstalments = due.Length, TotalRepayable = 1_000m * due.Length
        };
        var n = 1;
        foreach (var d in due)
            loan.Schedule.Add(new LoanScheduleEntry { LoanId = loan.Id, InstalmentNumber = n++, DueDate = d, InstalmentTotal = 1_000m, CapitalPortion = 1_000m });
        return (client, loan);
    }

    private static LoanCollection Collection(Loan loan, LoanScheduleEntry entry, CollectionStatus status, DateTime date, string? code = null) => new()
    {
        TenantId = loan.TenantId, LoanId = loan.Id, ScheduleEntryId = entry.Id, Channel = CollectionChannel.DebiCheck,
        Amount = entry.InstalmentTotal, ActionDate = date, Status = status,
        ReceivedUtc = status == CollectionStatus.Successful ? date.AddHours(6) : null, FailureCode = code
    };

    private static async Task<(AppDbContext, FakePortal, ClientPortalPublisher, Client)> ArrangeAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"portal-acct-{Guid.NewGuid()}").Options);
        var tenant = new Tenant { Name = "Albatross Money (Pty) Ltd", TradingName = "Albatross Money", SelfServiceCode = "GRN4KX" };
        db.Tenants.Add(tenant);

        var package = new CreditPackage { TenantId = tenant.Id, Name = "Standard" };
        db.CreditPackages.Add(package);

        var client = new Client { TenantId = tenant.Id, ClientNumber = "C-0001", FirstName = "Thandi", Surname = "Mokoena", IdNumber = IdNumber };
        db.Clients.Add(client);
        db.ClientAddresses.Add(new ClientAddress { ClientId = client.Id, AddressType = AddressType.Physical, MobileNumber = "082 123 4567" });

        var loan = new Loan
        {
            TenantId = tenant.Id, ClientId = client.Id, CreditPackageId = package.Id, LoanNumber = "LN-000001",
            Status = LoanStatus.Disbursed, Principal = 2_000m, NumberOfInstalments = 3, TotalRepayable = 3_000m
        };
        db.Loans.Add(loan);
        for (var i = 1; i <= 3; i++)
            db.LoanScheduleEntries.Add(new LoanScheduleEntry
            {
                LoanId = loan.Id, InstalmentNumber = i, DueDate = Today.AddMonths(i), InstalmentTotal = 1_000m, CapitalPortion = 1_000m
            });
        await db.SaveChangesAsync();

        var portal = new FakePortal();
        var publisher = new ClientPortalPublisher(db, portal,
            new OutboxMessageSender(db, NullLogger<OutboxMessageSender>.Instance), NullLogger<ClientPortalPublisher>.Instance);
        return (db, portal, publisher, client);
    }

    private sealed class FakePortal : IPortalApi
    {
        public List<PortalClientStatement> Sent { get; } = new();
        public List<Guid> Deleted { get; } = new();

        public bool IsConfigured => true;
        public string? LinkFor(string? code) => code is null ? null : $"https://portal.test/a/{code}";

        public Task PutClientAsync(PortalClientStatement statement, CancellationToken ct = default)
        {
            Sent.Add(statement);
            return Task.CompletedTask;
        }

        public Task DeleteClientAsync(Guid tenantId, Guid clientId, CancellationToken ct = default)
        {
            Deleted.Add(clientId);
            return Task.CompletedTask;
        }

        public Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortalFunnel?> FunnelAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
