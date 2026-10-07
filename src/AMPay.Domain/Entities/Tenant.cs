using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A customer trading under the AM-Pay ISV: an NCR-registered lender or a merchant.
/// AM-Pay itself exists as the single tenant with <see cref="IsPlatformOwner"/> set,
/// which is what gives the platform (sudo) user something to belong to.
/// Every tenant holds its own Netcash merchant account and its own set of service keys.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }

    /// <summary>NCR credit provider number, e.g. NCRCP9166. Null for non-lending merchants.</summary>
    public string? NcrNumber { get; set; }

    /// <summary>Netcash merchant account number: 11 digits, starts with 5 (N11).</summary>
    public string? NetcashAccountNumber { get; set; }

    /// <summary>True for AM-Pay Fintech itself. Exactly one tenant may set this.</summary>
    public bool IsPlatformOwner { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Onboarding;

    public string? ContactEmail { get; set; }
    public string? ContactNumber { get; set; }

    // ---- Shown on the contract as the credit provider's details ----

    public string? VatNumber { get; set; }
    public string? PhysicalAddress { get; set; }
    public string? PostalAddress { get; set; }

    /// <summary>The credit life underwriter, e.g. "Clientèle Life Assurance Company Limited (FSP 15268)".</summary>
    public string? CreditLifeUnderwriter { get; set; }

    /// <summary>The administrator, e.g. "Kepler Risk Services (Pty) Ltd".</summary>
    public string? CreditLifeAdministrator { get; set; }

    // Self-service portal.

    /// <summary>
    /// The code in the lender's public application link, e.g. GRN4KX in /a/GRN4KX. Null
    /// until the lender is given a link. Regenerating it kills the old link.
    /// </summary>
    public string? SelfServiceCode { get; set; }

    /// <summary>Cell number behind the portal's "Chat on WhatsApp" button. None shows no button.</summary>
    public string? AdvisorWhatsApp { get; set; }

    /// <summary>When the portal last accepted this lender's details and packages.</summary>
    public DateTime? PortalSyncedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    public ICollection<TenantServiceKey> ServiceKeys { get; set; } = new List<TenantServiceKey>();
    public ICollection<Client> Clients { get; set; } = new List<Client>();
}

/// <summary>
/// A Netcash service key held by a tenant.
/// <para>
/// The key itself is deliberately NOT stored here. <see cref="SecretName"/> is a pointer into
/// the secret store (user-secrets in development, Azure Key Vault in production) so that a
/// database dump never exposes live payment credentials. See INetcashSecretStore.
/// </para>
/// </summary>
public class TenantServiceKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public NetcashServiceId ServiceId { get; set; }

    /// <summary>Secret-store lookup name, e.g. "netcash:5123456789:1". Never the key value.</summary>
    public string SecretName { get; set; } = string.Empty;

    /// <summary>Last 4 characters of the GUID, for operator recognition in the UI. Not sensitive.</summary>
    public string? KeyHint { get; set; }

    public ServiceKeyStatus Status { get; set; } = ServiceKeyStatus.Unverified;
    public DateTime? LastValidatedUtc { get; set; }
    public string? LastValidationMessage { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Netcash requires revalidation at least every 24 hours and on each login.
    /// Three failures inside 10 minutes locks the merchant account, so callers must respect this.
    /// </summary>
    public bool NeedsRevalidation =>
        LastValidatedUtc is null || DateTime.UtcNow - LastValidatedUtc > TimeSpan.FromHours(24);
}
