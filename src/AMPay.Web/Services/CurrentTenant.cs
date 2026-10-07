using System.Security.Claims;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Resolves which tenant the current request is acting on.
/// <para>
/// This is the ISV boundary. A tenant user is pinned to their own tenant and cannot reach
/// another one. A platform (sudo) user has no tenant of their own and instead selects one to
/// work in, which is held in session.
/// </para>
/// </summary>
public interface ICurrentTenant
{
    /// <summary>The tenant this request operates on. Null when a platform user has selected none.</summary>
    Guid? TenantId { get; }

    string? TenantName { get; }

    /// <summary>True when the signed-in user is AM-Pay platform staff.</summary>
    bool IsPlatformUser { get; }

    /// <summary>Platform users only. Switch the tenant being worked on.</summary>
    Task SelectTenantAsync(Guid tenantId);

    Task LoadAsync();

    /// <summary>
    /// Throws unless the request may act on <paramref name="tenantId"/>. Call this on every
    /// action that loads tenant-owned data by id, or an id from the URL becomes a way across
    /// the boundary.
    /// </summary>
    void EnsureCanAccess(Guid tenantId);
}

public class CurrentTenant : ICurrentTenant
{
    /// <summary>
    /// Where a platform user's chosen tenant is remembered.
    /// <para>
    /// A cookie rather than session state, deliberately. In-memory session dies with the
    /// process, so on Azure every restart - and every request that lands on a second instance
    /// - would silently drop the operator back to "no customer selected" mid-capture.
    /// </para>
    /// <para>
    /// Nothing is trusted from this value: it only narrows what a platform user is looking at,
    /// and <see cref="EnsureCanAccess"/> still decides what they may reach. A tenant user
    /// ignores it entirely in favour of their own claim.
    /// </para>
    /// </summary>
    private const string SelectedTenantCookie = "ampay_tenant";

    private readonly IHttpContextAccessor _http;
    private readonly AppDbContext _db;

    private bool _loaded;

    public CurrentTenant(IHttpContextAccessor http, AppDbContext db)
    {
        _http = http;
        _db = db;
    }

    public Guid? TenantId { get; private set; }
    public string? TenantName { get; private set; }
    public bool IsPlatformUser { get; private set; }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return;

        IsPlatformUser = user.IsInRole(AppRoles.SuperAdmin);

        // A tenant user carries their tenant in a claim issued at sign-in, so the common
        // path costs no database round trip.
        var claim = user.FindFirstValue(AppClaims.TenantId);
        if (!IsPlatformUser)
        {
            if (Guid.TryParse(claim, out var ownTenant)) TenantId = ownTenant;
        }
        else
        {
            var selected = _http.HttpContext?.Request.Cookies[SelectedTenantCookie];
            if (Guid.TryParse(selected, out var chosen)) TenantId = chosen;
        }

        if (TenantId is not null)
        {
            TenantName = await _db.Tenants
                .Where(t => t.Id == TenantId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();

            // A stale session pointing at a deleted tenant should not leave the request
            // half-scoped.
            if (TenantName is null) TenantId = null;
        }
    }

    public async Task SelectTenantAsync(Guid tenantId)
    {
        if (!IsPlatformUser)
            throw new UnauthorizedAccessException("Only platform users may switch tenant.");

        var exists = await _db.Tenants.AnyAsync(t => t.Id == tenantId);
        if (!exists) throw new InvalidOperationException("Unknown tenant.");

        _http.HttpContext?.Response.Cookies.Append(SelectedTenantCookie, tenantId.ToString(),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                // Matches the auth cookie lifetime, so the selection cannot outlive the session
                // that made it.
                Expires = DateTimeOffset.UtcNow.AddHours(8),
                Secure = _http.HttpContext?.Request.IsHttps ?? false
            });

        TenantId = tenantId;
        TenantName = await _db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync();
    }

    public void EnsureCanAccess(Guid tenantId)
    {
        if (IsPlatformUser) return;

        if (TenantId is null || TenantId != tenantId)
            throw new UnauthorizedAccessException(
                "This record belongs to another customer account.");
    }
}

public static class AppClaims
{
    public const string TenantId = "ampay:tenant_id";
    public const string TenantName = "ampay:tenant_name";

    /// <summary>Present while the user is still on a password an administrator gave them.</summary>
    public const string MustChangePassword = "ampay:must_change_password";
}
