using System.Text;
using AMPay.Domain.Entities;
using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Tests;

public class BrandingTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46 };
    private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");

    [Fact]
    public void RealImages_AreRecognisedByTheirBytes()
    {
        Assert.Equal("image/png", TenantLogo.SniffContentType(Png));
        Assert.Equal("image/jpeg", TenantLogo.SniffContentType(Jpeg));
        Assert.Equal("image/webp", TenantLogo.SniffContentType(Webp));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><body>not an image</body></html>")]
    [InlineData("GIF89a")]
    [InlineData("")]
    public void AnythingElse_IsRefused_WhateverItIsCalled(string content) =>
        Assert.Null(TenantLogo.SniffContentType(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task TheLogo_TravelsToThePortal_WithThePackages()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"brand-{Guid.NewGuid()}").Options);
        var tenant = new Tenant { Name = "Green Nest Loans", SelfServiceCode = "GRN4KX" };
        db.Tenants.Add(tenant);
        db.TenantLogos.Add(new TenantLogo { TenantId = tenant.Id, ContentType = "image/png", Data = Png });
        db.CreditPackages.Add(new CreditPackage { TenantId = tenant.Id, Name = "Regular", MinLoanAmount = 500, MaxLoanAmount = 8000, MinTermMonths = 1, MaxTermMonths = 6 });
        await db.SaveChangesAsync();

        var portal = new RecordingPortal();
        var warning = await new PortalSync(db, portal).RefreshAsync(tenant.Id);

        Assert.Null(warning);
        Assert.Equal("GRN4KX", portal.Code);
        Assert.Equal("image/png", portal.Body!.LogoContentType);
        Assert.Equal(Png, Convert.FromBase64String(portal.Body.LogoBase64!));
        Assert.Single(portal.Body.Packages);
        Assert.NotNull((await db.Tenants.SingleAsync()).PortalSyncedUtc);
    }

    [Fact]
    public async Task ALenderWithoutALink_IsNotSent()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"brand-{Guid.NewGuid()}").Options);
        var tenant = new Tenant { Name = "No Link Yet" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var portal = new RecordingPortal();
        await new PortalSync(db, portal).RefreshAsync(tenant.Id);

        Assert.Null(portal.Body);
    }

    private sealed class RecordingPortal : IPortalApi
    {
        public string? Code { get; private set; }
        public PortalLenderSync? Body { get; private set; }
        public bool IsConfigured => true;
        public string? LinkFor(string? code) => code;
        public Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default)
        {
            Code = code;
            Body = body;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
