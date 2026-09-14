using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Netcash;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Services;

/// <summary>Why a Netcash service is or is not usable for a given customer.</summary>
public enum CapabilityState
{
    /// <summary>Stubs are on. Usable, but nothing reaches Netcash.</summary>
    Stubbed,

    /// <summary>A key slot exists, the secret resolves, and Netcash has validated it.</summary>
    Ready,

    /// <summary>The secret resolves but has never been validated against Netcash.</summary>
    ReadyUnvalidated,

    /// <summary>The customer has no key slot for this service.</summary>
    NoSlot,

    /// <summary>A slot exists but no value is loaded in the secret store.</summary>
    SecretMissing,

    /// <summary>The slot exists but has been deactivated.</summary>
    Inactive,

    /// <summary>Netcash rejected this key the last time it was validated.</summary>
    Rejected,

    /// <summary>The customer has no Netcash merchant account number.</summary>
    NoMerchantAccount
}

/// <summary>
/// The answer to "can this customer use this Netcash service right now, and if not, what
/// exactly is missing".
/// </summary>
public class NetcashCapability
{
    public NetcashServiceId ServiceId { get; init; }
    public CapabilityState State { get; init; }

    /// <summary>The resolved service key. Null whenever <see cref="IsUsable"/> is false.</summary>
    public string? ServiceKey { get; init; }

    /// <summary>Plain-language explanation for the operator.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>What the operator should do about it. Null when nothing is wrong.</summary>
    public string? Remedy { get; init; }

    public bool IsUsable => State is CapabilityState.Stubbed
        or CapabilityState.Ready
        or CapabilityState.ReadyUnvalidated;

    /// <summary>True when the work will complete but nothing actually reaches Netcash.</summary>
    public bool IsSimulated => State == CapabilityState.Stubbed;

    public string FriendlyServiceName => ServiceId switch
    {
        NetcashServiceId.DebitOrders => "Debit orders and DebiCheck",
        NetcashServiceId.PayNow => "Pay Now",
        NetcashServiceId.Account => "Account services",
        NetcashServiceId.SalaryPayments => "Salary payments",
        NetcashServiceId.CreditorPayments => "Creditor payments",
        NetcashServiceId.RiskReports => "Risk reports",
        _ => ServiceId.ToString()
    };
}

/// <summary>
/// Decides whether a Netcash service is configured for a customer, and resolves its key.
/// <para>
/// Every call site goes through here rather than resolving keys for itself, so there is one
/// place that decides what "configured" means - and one place that can never hand a caller a
/// placeholder key while believing it is real.
/// </para>
/// </summary>
public interface INetcashCapabilityService
{
    Task<NetcashCapability> GetAsync(
        Guid tenantId, NetcashServiceId serviceId, CancellationToken ct = default);

    /// <summary>Every service this customer has a slot for, for an at-a-glance readiness view.</summary>
    Task<IReadOnlyList<NetcashCapability>> GetAllAsync(
        Guid tenantId, CancellationToken ct = default);
}

public class NetcashCapabilityService : INetcashCapabilityService
{
    private readonly AppDbContext _db;
    private readonly INetcashSecretStore _secrets;
    private readonly NetcashOptions _options;

    public NetcashCapabilityService(
        AppDbContext db, INetcashSecretStore secrets, IOptions<NetcashOptions> options)
    {
        _db = db;
        _secrets = secrets;
        _options = options.Value;
    }

    public async Task<NetcashCapability> GetAsync(
        Guid tenantId, NetcashServiceId serviceId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.NetcashAccountNumber })
            .FirstOrDefaultAsync(ct);

        var slot = await _db.TenantServiceKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.ServiceId == serviceId, ct);

        // Stub mode short-circuits everything. Development and testing must not depend on
        // credentials that only exist in production, so a missing slot is not an obstacle here.
        if (_options.UseStubs)
        {
            return new NetcashCapability
            {
                ServiceId = serviceId,
                State = CapabilityState.Stubbed,
                ServiceKey = slot is not null
                    ? await _secrets.GetServiceKeyAsync(slot.SecretName, ct) ?? PlaceholderKey(serviceId)
                    : PlaceholderKey(serviceId),
                Message = "Running on stubs - this will complete locally, but nothing reaches Netcash.",
                Remedy = null
            };
        }

        // From here on, stubs are off: a real call is about to be made, so anything less than
        // a genuine key is a hard stop. Sending a placeholder would draw Netcash error 100,
        // and three of those inside ten minutes locks the merchant account.

        if (tenant is null)
            return Fail(serviceId, CapabilityState.NoMerchantAccount,
                "This customer account no longer exists.", null);

        if (string.IsNullOrWhiteSpace(tenant.NetcashAccountNumber))
            return Fail(serviceId, CapabilityState.NoMerchantAccount,
                $"{tenant.Name} has no Netcash merchant account number.",
                "Set the Netcash account number on the customer record.");

        if (slot is null)
            return Fail(serviceId, CapabilityState.NoSlot,
                $"No {Friendly(serviceId)} service key is set up for {tenant.Name}.",
                "Add a key slot on the Netcash service keys screen, then load the key value.");

        if (!slot.IsActive)
            return Fail(serviceId, CapabilityState.Inactive,
                $"The {Friendly(serviceId)} service key for {tenant.Name} is deactivated.",
                "Reactivate it on the Netcash service keys screen.");

        var value = await _secrets.GetServiceKeyAsync(slot.SecretName, ct);

        if (string.IsNullOrWhiteSpace(value))
            return Fail(serviceId, CapabilityState.SecretMissing,
                $"The {Friendly(serviceId)} service key for {tenant.Name} has not been loaded.",
                $"Load it into the secret store under \"{slot.SecretName}\" - " +
                "user-secrets in development, Key Vault in production.");

        // A key Netcash has already rejected will be rejected again. Stopping here keeps a
        // known-bad key from spending one of the three attempts before lockout.
        if (slot.Status is ServiceKeyStatus.NoActiveService
            or ServiceKeyStatus.NoActiveServiceKey
            or ServiceKeyStatus.AuthenticationFailed)
        {
            return Fail(serviceId, CapabilityState.Rejected,
                $"Netcash rejected this {Friendly(serviceId)} key when it was last validated " +
                $"({slot.Status}).",
                slot.LastValidationMessage
                ?? "Check the key value with Netcash, reload it, then validate again.");
        }

        var validated = slot.Status == ServiceKeyStatus.Validated && !slot.NeedsRevalidation;

        return new NetcashCapability
        {
            ServiceId = serviceId,
            State = validated ? CapabilityState.Ready : CapabilityState.ReadyUnvalidated,
            ServiceKey = value,
            Message = validated
                ? $"{Friendly(serviceId)} is configured and validated."
                : $"{Friendly(serviceId)} is configured but has not been validated with Netcash " +
                  "in the last 24 hours.",
            Remedy = validated
                ? null
                : "Validate the service keys to confirm Netcash still accepts them."
        };
    }

    public async Task<IReadOnlyList<NetcashCapability>> GetAllAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var services = await _db.TenantServiceKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .Select(k => k.ServiceId)
            .ToListAsync(ct);

        var results = new List<NetcashCapability>();
        foreach (var service in services.OrderBy(s => s))
            results.Add(await GetAsync(tenantId, service, ct));

        return results;
    }

    private static NetcashCapability Fail(
        NetcashServiceId serviceId, CapabilityState state, string message, string? remedy) =>
        new()
        {
            ServiceId = serviceId,
            State = state,
            ServiceKey = null, // never hand back a placeholder when a real call is expected
            Message = message,
            Remedy = remedy
        };

    /// <summary>
    /// Used only in stub mode, so that a customer with no slots at all is still fully usable
    /// in development. Never returned once stubs are off.
    /// </summary>
    private static string PlaceholderKey(NetcashServiceId serviceId) =>
        $"STUB-{serviceId}-KEY";

    private static string Friendly(NetcashServiceId serviceId) => serviceId switch
    {
        NetcashServiceId.DebitOrders => "debit order",
        NetcashServiceId.PayNow => "Pay Now",
        NetcashServiceId.Account => "account",
        NetcashServiceId.SalaryPayments => "salary payment",
        NetcashServiceId.CreditorPayments => "creditor payment",
        NetcashServiceId.RiskReports => "risk report",
        _ => serviceId.ToString()
    };
}
