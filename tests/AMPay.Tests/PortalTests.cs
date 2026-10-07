using System.Security.Cryptography;
using AMPay.Domain.Documents;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AMPay.Tests;

public class PortalTests
{
    private const string IdNumber = "8501015800088"; // fictional, valid check digit

    // ----------------------------------------------------------------- shared helpers

    [Theory]
    [InlineData("082 123 4567", "27821234567")]
    [InlineData("+27 82 123 4567", "27821234567")]
    [InlineData("27821234567", "27821234567")]
    [InlineData("0711234567", "27711234567")]
    [InlineData("0611234567", "27611234567")]
    [InlineData("011 894 1087", null)]   // a landline cannot receive a code
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void CellNumbers_AreNormalisedOrRejected(string input, string? expected) =>
        Assert.Equal(expected, PortalApi.NormaliseMobile(input));

    [Fact]
    public void CellNumbers_DisplayTheWayPeopleWriteThem() =>
        Assert.Equal("082 123 4567", PortalApi.DisplayMobile("27821234567"));

    [Fact]
    public void LenderCodes_AreSixUnambiguousCharacters()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var code = PortalApi.NewLenderCode();
            Assert.True(PortalApi.IsValidLenderCode(code), code);
            Assert.DoesNotContain(code, c => "01OIL".Contains(c));
        }
    }

    [Theory]
    [InlineData("GRN4KX", true)]
    [InlineData("grn4kx", false)]   // codes are upper case; the portal upper-cases before checking
    [InlineData("GRN4K", false)]
    [InlineData("GRN0KX", false)]   // 0 is not in the alphabet
    [InlineData("../etc", false)]
    public void LenderCodes_AreValidatedStrictly(string code, bool valid) =>
        Assert.Equal(valid, PortalApi.IsValidLenderCode(code));

    // ----------------------------------------------------------------- importing an application

    [Fact]
    public async Task ANewApplicant_BecomesACapturedClient_WithDocumentsForReview()
    {
        var (db, portal, importer, tenantId) = await ArrangeAsync();

        var result = await importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "user-1");

        Assert.True(result.Created);
        Assert.Equal(2, result.DocumentsCopied);
        Assert.Empty(result.Warnings);

        var c = await db.Clients.Include(x => x.Financial).Include(x => x.Addresses).Include(x => x.OtherDetails)
            .Include(x => x.Documents).Include(x => x.Payback).SingleAsync();
        Assert.Equal("Thandi", c.FirstName);
        Assert.Equal(IdNumber, c.IdNumber);
        Assert.Equal(ClientStatus.PendingVerification, c.Status);
        Assert.Equal(14_000m, c.Financial!.NetMonthlyIncome);
        Assert.Equal("082 123 4567", c.Addresses.Single().MobileNumber);
        Assert.True(c.OtherDetails!.DataProcessingConsent);
        Assert.Equal(5_000m, c.Payback!.LoanAmount);
        Assert.All(c.Documents, d => Assert.Equal(DocumentReviewStatus.Pending, d.ReviewStatus));

        // The portal is told only after AM-Pay holds everything.
        Assert.Equal(c.Id, portal.PickedUp?.AmpayClientId);
        Assert.Equal(1, await db.ClientNotes.CountAsync());
    }

    [Fact]
    public async Task AReturningClient_IsCompleted_NotOverwritten()
    {
        var (db, portal, importer, tenantId) = await ArrangeAsync();
        var existing = new Client
        {
            TenantId = tenantId, ClientNumber = "0007", FirstName = "Thandi", Surname = "Nkosi", IdNumber = IdNumber,
            Status = ClientStatus.Active,
            Financial = new ClientFinancial { NetMonthlyIncome = 12_345m }
        };
        db.Clients.Add(existing);
        await db.SaveChangesAsync();

        var result = await importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "user-1");

        Assert.False(result.Created);
        Assert.Equal(existing.Id, result.Client.Id);
        Assert.Equal(1, await db.Clients.CountAsync());

        var c = await db.Clients.Include(x => x.Financial).SingleAsync();
        Assert.Equal(12_345m, c.Financial!.NetMonthlyIncome);   // what was on file stays
        Assert.Equal(ClientStatus.Active, c.Status);              // an active client is not demoted
        Assert.Equal(2, await db.ClientDocuments.CountAsync());
    }

    [Fact]
    public async Task ImportingTwice_DoesNotDuplicateDocuments()
    {
        var (db, portal, importer, tenantId) = await ArrangeAsync();

        await importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "user-1");
        var again = await importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "user-1");

        Assert.Equal(0, again.DocumentsCopied);
        Assert.Equal(2, await db.ClientDocuments.CountAsync());
    }

    [Fact]
    public async Task ADocumentThatFailsItsFingerprint_IsNotImported()
    {
        var (db, portal, importer, tenantId) = await ArrangeAsync();
        portal.Files[portal.Detail.Documents[0].Id] = "%PDF-tampered"u8.ToArray();

        var result = await importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "user-1");

        Assert.Equal(1, result.DocumentsCopied);
        Assert.Contains(result.Warnings, w => w.Contains("fingerprint"));
    }

    [Fact]
    public async Task ADraftOrInvalidApplication_IsRefused()
    {
        var (_, portal, importer, tenantId) = await ArrangeAsync(status: PortalApplicationStatus.Draft);
        await Assert.ThrowsAsync<PortalUnavailableException>(() => importer.ImportAsync(tenantId, portal.Detail.Summary.Id, "u"));

        var (_, portal2, importer2, tenant2) = await ArrangeAsync(idNumber: "1234567890123");
        await Assert.ThrowsAsync<PortalUnavailableException>(() => importer2.ImportAsync(tenant2, portal2.Detail.Summary.Id, "u"));
    }

    // ----------------------------------------------------------------- fixture

    private static async Task<(AppDbContext, FakePortal, PortalImporter, Guid)> ArrangeAsync(
        PortalApplicationStatus status = PortalApplicationStatus.Submitted, string idNumber = IdNumber)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"portal-{Guid.NewGuid()}").Options);
        var tenant = new Tenant { Name = "Green Nest Loans", SelfServiceCode = "GRN4KX" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var idDoc = "%PDF-1.4 id document"u8.ToArray();
        var payslip = "%PDF-1.4 payslip"u8.ToArray();
        var docs = new List<PortalDocument>
        {
            new(Guid.NewGuid(), DocumentType.IdDocument, "id.pdf", "application/pdf", idDoc.Length, Sha(idDoc), DateTime.UtcNow),
            new(Guid.NewGuid(), DocumentType.Payslip, "payslip.pdf", "application/pdf", payslip.Length, Sha(payslip), DateTime.UtcNow)
        };

        var summary = new PortalApplicationSummary(Guid.NewGuid(), "APP-7K2Q9M", tenant.Id, status, "Thandi", "Nkosi", idNumber,
            "27821234567", 5_000m, 3, docs.Count, DateTime.UtcNow, DateTime.UtcNow, null, null);

        var detail = new PortalApplicationDetail(summary, "Ms", null, "thandi@example.test", "1 Example Road", null, "Primrose",
            "Germiston", "Gauteng", "1401", "Example Retail", "Cashier", new DateTime(2021, 3, 1), "Monthly", 25,
            18_000m, 14_000m, 0m, 6_500m, 900m, "Regular", 2_100m, true, true, false, DateTime.UtcNow, docs);

        var portal = new FakePortal(detail);
        portal.Files[docs[0].Id] = idDoc;
        portal.Files[docs[1].Id] = payslip;

        return (db, portal, new PortalImporter(db, portal, new MemoryStore(), NullLogger<PortalImporter>.Instance), tenant.Id);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private sealed class FakePortal : IPortalApi
    {
        public FakePortal(PortalApplicationDetail detail) => Detail = detail;
        public PortalApplicationDetail Detail { get; }
        public Dictionary<Guid, byte[]> Files { get; } = new();
        public PortalPickedUp? PickedUp { get; private set; }

        public bool IsConfigured => true;
        public string? LinkFor(string? code) => code is null ? null : $"https://portal.test/a/{code}";
        public Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PortalApplicationSummary>>(new[] { Detail.Summary });
        public Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
            Task.FromResult(tenantId == Detail.Summary.TenantId && id == Detail.Summary.Id ? Detail : null);
        public Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default) =>
            Task.FromResult(Files.TryGetValue(documentId, out var b) ? b : null);
        public Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default)
        {
            PickedUp = body;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PortalCallback>>(Array.Empty<PortalCallback>());
        public Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryStore : IDocumentStore
    {
        public async Task<StoredDocument> SaveAsync(Guid tenantId, Guid clientId, string fileName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            return new StoredDocument($"{Guid.NewGuid():N}", Sha(ms.ToArray()), ms.Length);
        }
        public Task<Stream?> OpenAsync(string storagePath, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storagePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
