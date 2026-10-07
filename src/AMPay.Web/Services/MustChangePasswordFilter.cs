using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AMPay.Web.Services;

/// <summary>
/// Keeps a user on a password an administrator chose - a new account, or a reset - away from
/// everything except changing it. The administrator knows that password; until it is
/// replaced, nothing done under it can be said to be the user's own act.
/// <para>
/// Reads a claim, not the database, so it costs nothing per request. The claim is refreshed
/// when the password changes and by the security-stamp check, so a reset takes effect on
/// already-signed-in sessions within minutes.
/// </para>
/// </summary>
public class MustChangePasswordFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        var controller = context.RouteData.Values["controller"]?.ToString();

        // Signing in and out, changing the password and the public signing pages stay open.
        var exempt = string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(controller, "Sign", StringComparison.OrdinalIgnoreCase);

        if (!exempt && user.Identity?.IsAuthenticated == true && user.HasClaim(c => c.Type == AppClaims.MustChangePassword))
        {
            context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
            return Task.CompletedTask;
        }

        return next();
    }
}
