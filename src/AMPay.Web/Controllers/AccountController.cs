using System.ComponentModel.DataAnnotations;
using AMPay.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AMPay.Web.Controllers;

[AllowAnonymous]
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

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["Title"] = "Sign in";
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

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
