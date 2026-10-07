using System.ComponentModel.DataAnnotations;
using AMPay.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AMPay.Web.Controllers;

// Anonymous per action, not per class: a class-level [AllowAnonymous] would override the
// [Authorize] on ChangePassword and serve it to signed-out visitors.
public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<AccountController> _log;

    public AccountController(
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users,
        ILogger<AccountController> log)
    {
        _signIn = signIn;
        _users = users;
        _log = log;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["Title"] = "Sign in";
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        ViewData["Title"] = "Sign in";
        if (!ModelState.IsValid) return View(model);

        var result = await _signIn.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var user = await _users.FindByEmailAsync(model.Email);
            if (user is not null)
            {
                if (!user.IsActive)
                {
                    await _signIn.SignOutAsync();
                    ModelState.AddModelError(string.Empty, "This account has been deactivated.");
                    return View(model);
                }

                user.LastLoginUtc = DateTime.UtcNow;
                await _users.UpdateAsync(user);
            }

            _log.LogInformation("User {Email} signed in.", model.Email);

            if (user?.MustChangePassword == true)
                return RedirectToAction(nameof(ChangePassword));

            // Never redirect to an absolute URL supplied in the query string.
            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);

            return RedirectToAction("Index", "Home");
        }

        if (result.IsLockedOut)
        {
            _log.LogWarning("Account {Email} is locked out.", model.Email);
            ModelState.AddModelError(string.Empty,
                "This account is temporarily locked after too many failed attempts. Try again in 15 minutes.");
            return View(model);
        }

        // Deliberately does not say which half was wrong.
        ModelState.AddModelError(string.Empty, "Incorrect email address or password.");
        return View(model);
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        HttpContext.Session.Clear();

        // The next person at this browser must not inherit the previous operator's customer.
        Response.Cookies.Delete("ampay_tenant");

        return RedirectToAction(nameof(Login));
    }

    // ---------------------------------------------------------------- password

    [HttpGet]
    [Authorize]
    public IActionResult ChangePassword()
    {
        ViewData["Title"] = "Change password";
        ViewBag.Forced = User.HasClaim(c => c.Type == Services.AppClaims.MustChangePassword);
        return View(new ChangePasswordViewModel());
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        ViewData["Title"] = "Change password";
        ViewBag.Forced = User.HasClaim(c => c.Type == Services.AppClaims.MustChangePassword);

        if (model.NewPassword == model.CurrentPassword)
            ModelState.AddModelError(nameof(model.NewPassword), "Choose a password different from the current one.");

        if (!ModelState.IsValid) return View(model);

        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToAction(nameof(Login));

        var result = await _users.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(
                    e.Code == "PasswordMismatch" ? nameof(model.CurrentPassword) : nameof(model.NewPassword),
                    e.Code == "PasswordMismatch" ? "The current password is not correct." : e.Description);
            return View(model);
        }

        user.MustChangePassword = false;
        user.PasswordChangedUtc = DateTime.UtcNow;
        await _users.UpdateAsync(user);

        // New cookie without the must-change claim. Changing the password also rotated the
        // security stamp, so sessions elsewhere on the old password end at their next check.
        await _signIn.RefreshSignInAsync(user);

        _log.LogInformation("User {Email} changed their password.", user.Email);

        TempData["Success"] = "Your password has been changed.";
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Denied()
    {
        ViewData["Title"] = "Access denied";
        return View();
    }
}

public class LoginViewModel
{
    [Required, EmailAddress, Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Display(Name = "New password")]
    [StringLength(100, MinimumLength = 12, ErrorMessage = "Use at least 12 characters.")]
    public string NewPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Display(Name = "Confirm new password")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two new passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
