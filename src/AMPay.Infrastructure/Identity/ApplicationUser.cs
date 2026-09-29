using Microsoft.AspNetCore.Identity;

namespace AMPay.Infrastructure.Identity;

/// <summary>
/// A platform user.
/// <para>
/// <see cref="TenantId"/> is what separates the two kinds of user. A platform user
/// (the sudo account) has no tenant and can act across all of them. Everyone else is
/// bound to exactly one tenant and can only ever see that tenant data.
/// </para>
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Null for platform users. Set for every tenant user.</summary>
    public Guid? TenantId { get; set; }

    public string FullName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>True when this user is not scoped to a tenant, i.e. AM-Pay platform staff.</summary>
    public bool IsPlatformUser => TenantId is null;
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string name) : base(name) { }

    public string? Description { get; set; }
}

/// <summary>
/// The four roles the application recognises.
/// <para>
/// This is the ISV shape: AM-Pay sits above, its customers sit below, and a customer
/// administrator can run their own shop without ever seeing another customer data.
/// </para>
/// </summary>
public static class AppRoles
{
    /// <summary>AM-Pay platform owner. Cross-tenant: creates tenants, manages service keys, sees everything.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Administers a single tenant: its users, its service keys, its clients.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Captures clients and originates mandates within one tenant.</summary>
    public const string Capturer = "Capturer";

    /// <summary>Read-only access within one tenant.</summary>
    public const string Viewer = "Viewer";

    /// <summary>
    /// Verifies supporting documents and nothing else. Sees a client's identifying details
    /// and their uploaded files so the two can be compared, but cannot capture, amend,
    /// originate a mandate or raise a loan.
    /// <para>
    /// The narrowness is the point. Verification is a control, and a control performed by
    /// the same person who captured the file is not a control.
    /// </para>
    /// </summary>
    public const string Reviewer = "Reviewer";

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [SuperAdmin] = "AM-Pay platform administrator. Full cross-tenant access.",
        [TenantAdmin] = "Administers one customer account, its users and its Netcash service keys.",
        [Capturer] = "Onboards clients and originates DebiCheck mandates.",
        [Reviewer] = "Verifies client documents only. No capture, no mandates, no lending.",
        [Viewer] = "Read-only access to one customer account."
    };
}

/// <summary>Authorisation policy names, so controllers never hard-code role strings.</summary>
public static class AppPolicies
{
    /// <summary>Platform-only operations: tenant management, cross-tenant reporting.</summary>
    public const string PlatformOnly = "PlatformOnly";

    /// <summary>Administering a tenant. Satisfied by SuperAdmin or that tenant TenantAdmin.</summary>
    public const string TenantAdministration = "TenantAdministration";

    /// <summary>Creating or changing client and mandate data.</summary>
    public const string CanCapture = "CanCapture";

    /// <summary>
    /// Approving or rejecting supporting documents. Satisfied by a Reviewer, and by the
    /// administrator roles so that a small office is not locked out of its own queue.
    /// Deliberately NOT satisfied by Capturer.
    /// </summary>
    public const string CanReviewDocuments = "CanReviewDocuments";

    /// <summary>
    /// Seeing a client file and its documents. Everyone who works a client can read one.
    /// </summary>
    public const string CanViewClients = "CanViewClients";

    /// <summary>
    /// Taking the credit decision: approving, declining, overriding an affordability flag,
    /// marking a loan disbursed, and repricing the credit packages. A capturer quotes and
    /// submits; an administrator decides.
    /// </summary>
    public const string CanApproveCredit = "CanApproveCredit";
}
