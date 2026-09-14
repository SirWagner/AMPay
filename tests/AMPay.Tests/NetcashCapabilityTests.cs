using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Netcash;
using AMPay.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Tests;

/// <summary>
/// The capability service decides whether a real Netcash call may be made. The case that
/// matters most is the negative one: when a key is missing it must refuse, and must never
/// hand back a placeholder. Sending a bogus service key draws Netcash error 100, and three
/// of those inside ten minutes locks the merchant account - so a configuration gap would
/// present as a locked account rather than as a missing key.
/// </summary>
public class NetcashCapabilityTests
{
    private const string LoadedKey = "c74e1115-5499-4663-85fb-2a6bbbbfb9ef";

    // ---------------------------------------------------------------- stub mode

    [Fact]
    public async Task Stub_mode_is_usable_even_with_nothing_configured()
    {
        var (db, tenantId) = await NewDbAsync(withAccountNumber: false, slots: Array.Empty<NetcashServiceId>());
        var service = Build(db, useStubs: true, loadedSecrets: new Dictionary<string, string>());

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        Assert.True(result.IsUsable);
        Assert.True(result.IsSimulated);
        Assert.Equal(CapabilityState.Stubbed, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.ServiceKey));
    }

    // ------------------------------------------------------ the refusals that matter

    [Fact]
    public async Task Live_mode_refuses_when_the_secret_is_not_loaded()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        // Slot exists, value does not.
        var service = Build(db, useStubs: false, loadedSecrets: new Dictionary<string, string>());

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        Assert.False(result.IsUsable);
        Assert.Equal(CapabilityState.SecretMissing, result.State);

        // The important assertion: no placeholder is handed back.
        Assert.Null(result.ServiceKey);
        Assert.Contains("secret store", result.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_mode_refuses_when_there_is_no_slot_for_the_service()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [SecretName(tenantId, NetcashServiceId.DebitOrders)] = LoadedKey });

        // Pay Now was never set up for this customer.
        var result = await service.GetAsync(tenantId, NetcashServiceId.PayNow);

        Assert.False(result.IsUsable);
        Assert.Equal(CapabilityState.NoSlot, result.State);
        Assert.Null(result.ServiceKey);
    }

    [Fact]
    public async Task Live_mode_refuses_when_the_customer_has_no_netcash_account_number()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: false, slots: new[] { NetcashServiceId.DebitOrders });

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [SecretName(tenantId, NetcashServiceId.DebitOrders)] = LoadedKey });

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        Assert.False(result.IsUsable);
        Assert.Equal(CapabilityState.NoMerchantAccount, result.State);
        Assert.Null(result.ServiceKey);
    }

    [Fact]
    public async Task A_key_netcash_already_rejected_is_not_retried()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        var slot = await db.TenantServiceKeys.FirstAsync();
        slot.Status = ServiceKeyStatus.NoActiveServiceKey;
        slot.LastValidatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [slot.SecretName] = LoadedKey });

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        // Retrying a key Netcash has already rejected spends one of the three attempts
        // before the merchant account locks out, for no possible gain.
        Assert.False(result.IsUsable);
        Assert.Equal(CapabilityState.Rejected, result.State);
        Assert.Null(result.ServiceKey);
    }

    [Fact]
    public async Task Live_mode_refuses_a_deactivated_slot()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        var slot = await db.TenantServiceKeys.FirstAsync();
        slot.IsActive = false;
        await db.SaveChangesAsync();

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [slot.SecretName] = LoadedKey });

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        Assert.False(result.IsUsable);
        Assert.Equal(CapabilityState.Inactive, result.State);
    }

    // ---------------------------------------------------------------- the happy paths

    [Fact]
    public async Task A_loaded_and_validated_key_is_ready()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        var slot = await db.TenantServiceKeys.FirstAsync();
        slot.Status = ServiceKeyStatus.Validated;
        slot.LastValidatedUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [slot.SecretName] = LoadedKey });

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        Assert.True(result.IsUsable);
        Assert.False(result.IsSimulated);
        Assert.Equal(CapabilityState.Ready, result.State);
        Assert.Equal(LoadedKey, result.ServiceKey);
        Assert.Null(result.Remedy);
    }

    [Fact]
    public async Task A_loaded_but_stale_key_is_usable_and_says_so()
    {
        var (db, tenantId) = await NewDbAsync(
            withAccountNumber: true, slots: new[] { NetcashServiceId.DebitOrders });

        var slot = await db.TenantServiceKeys.FirstAsync();
        slot.Status = ServiceKeyStatus.Validated;

        // Netcash asks for revalidation at least every 24 hours.
        slot.LastValidatedUtc = DateTime.UtcNow.AddHours(-30);
        await db.SaveChangesAsync();

        var service = Build(db, useStubs: false,
            loadedSecrets: new Dictionary<string, string> { [slot.SecretName] = LoadedKey });

        var result = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);

        // Work is not blocked, but the operator is told.
        Assert.True(result.IsUsable);
        Assert.Equal(CapabilityState.ReadyUnvalidated, result.State);
        Assert.Equal(LoadedKey, result.ServiceKey);
        Assert.NotNull(result.Remedy);
    }

    [Fact]
    public async Task One_service_being_configured_does_not_unblock_another()
    {
        var (db, tenantId) = await NewDbAsync(withAccountNumber: true,
            slots: new[] { NetcashServiceId.DebitOrders, NetcashServiceId.PayNow });

        // Only the debit order key is loaded.
        var service = Build(db, useStubs: false, loadedSecrets: new Dictionary<string, string>
        {
            [SecretName(tenantId, NetcashServiceId.DebitOrders)] = LoadedKey
        });

        var debitOrders = await service.GetAsync(tenantId, NetcashServiceId.DebitOrders);
        var payNow = await service.GetAsync(tenantId, NetcashServiceId.PayNow);

        Assert.True(debitOrders.IsUsable);
        Assert.False(payNow.IsUsable);
        Assert.Equal(CapabilityState.SecretMissing, payNow.State);
    }

    // ---------------------------------------------------------------- helpers

    private static string SecretName(Guid tenantId, NetcashServiceId serviceId) =>
        $"Netcash:ServiceKeys:{tenantId}:{(int)serviceId}";

    private static async Task<(AppDbContext Db, Guid TenantId)> NewDbAsync(
        bool withAccountNumber, NetcashServiceId[] slots)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"capability-{Guid.NewGuid()}")
            .Options;

        var db = new AppDbContext(options);

        var tenant = new Tenant
        {
            Name = "Albatross Money",
            NcrNumber = "NCRCP9166",
            NetcashAccountNumber = withAccountNumber ? "51234567890" : null,
            Status = TenantStatus.Active
        };

        db.Tenants.Add(tenant);

        foreach (var serviceId in slots)
        {
            db.TenantServiceKeys.Add(new TenantServiceKey
            {
                TenantId = tenant.Id,
                ServiceId = serviceId,
                SecretName = SecretName(tenant.Id, serviceId),
                Status = ServiceKeyStatus.Unverified
            });
        }

        await db.SaveChangesAsync();
        return (db, tenant.Id);
    }

    private static NetcashCapabilityService Build(
        AppDbContext db, bool useStubs, Dictionary<string, string> loadedSecrets) =>
        new(db,
            new FakeSecretStore(loadedSecrets),
            Options.Create(new NetcashOptions { UseStubs = useStubs }));

    /// <summary>Stands in for user-secrets or Key Vault: it knows only what has been loaded.</summary>
    private sealed class FakeSecretStore : INetcashSecretStore
    {
        private readonly Dictionary<string, string> _secrets;
        public FakeSecretStore(Dictionary<string, string> secrets) => _secrets = secrets;

        public Task<string?> GetServiceKeyAsync(string secretName, CancellationToken ct = default) =>
            Task.FromResult(_secrets.TryGetValue(secretName, out var v) ? v : null);

        public Task<string> GetSoftwareVendorKeyAsync(CancellationToken ct = default) =>
            Task.FromResult("24ade73c-98cf-47b3-99be-cc7b867b3080");
    }
}
