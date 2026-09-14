using System.Security.Claims;
using AMPay.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Services;

/// <summary>
/// Stamps the user tenant onto their identity at sign-in, so tenant scoping on every later
/// request is a claim read rather than a database lookup.
/// </summary>
public class TenantClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    public TenantClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.TenantId is not null)
            identity.AddClaim(new Claim(AppClaims.TenantId, user.TenantId.Value.ToString()));

        if (!string.IsNullOrWhiteSpace(user.FullName))
            identity.AddClaim(new Claim("ampay:full_name", user.FullName));

        return identity;
    }
}
