using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// User administration.
/// <para>
/// A customer administrator can only create users inside their own customer account, and can
/// never grant SuperAdmin. Only a platform user can do either - that boundary is enforced
/// here rather than in the view, because a hidden field is not a permission check.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class UsersController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICurrentTenant _tenant;
    private readonly ILogger<UsersController> _log;

    public UsersController(
        AppDbContext db,
        UserManager<ApplicationUser> users,
        ICurrentTenant tenant,
        ILogger<UsersController> log)
    {
        _db = db;
        _users = users;
        _tenant = tenant;
        _log = log;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Users";
        await _tenant.LoadAsync();

        var query = _db.Users.AsNoTracking().AsQueryable();

        // A tenant admin sees only their own people.
        if (!_tenant.IsPlatformUser)
            query = query.Where(u => u.TenantId == _tenant.TenantId);

        var users = await query.OrderBy(u => u.FullName).ToListAsync();

        var roles = new Dictionary<Guid, string>();
        var tenantNames = await _db.Tenants.AsNoTracking()
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        foreach (var u in users)
        {
            var r = await _users.GetRolesAsync(u);
            roles[u.Id] = r.FirstOrDefault() ?? "-";
        }

        ViewBag.Roles = roles;
        ViewBag.TenantNames = tenantNames;
        return View(users);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New user";
        await _tenant.LoadAsync();

        return View(await BuildCreateModelAsync(new CreateUserModel
        {
            TenantId = _tenant.TenantId
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserModel model)
    {
        ViewData["Title"] = "New user";
        await _tenant.LoadAsync();

        // Escalation guards. Both of these are reachable by editing the form, so neither can
        // live in the view alone.
        if (!_tenant.IsPlatformUser)
        {
            if (model.Role == AppRoles.SuperAdmin)
                ModelState.AddModelError(nameof(model.Role),
                    "Only a platform administrator can create a super administrator.");

            // Pin to the caller's own tenant regardless of what was posted.
            model.TenantId = _tenant.TenantId;
        }

        if (model.Role != AppRoles.SuperAdmin && model.TenantId is null)
            ModelState.AddModelError(nameof(model.TenantId),
                "Choose the customer account this user belongs to.");

        if (await _users.FindByEmailAsync(model.Email) is not null)
            ModelState.AddModelError(nameof(model.Email), "That email address is already in use.");

        if (!ModelState.IsValid)
            return View(await BuildCreateModelAsync(model));

        var user = new ApplicationUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            EmailConfirmed = true,
            FullName = model.FullName.Trim(),
            // The administrator chose this password, so the user must replace it first.
            MustChangePassword = true,
            // A super admin is deliberately unscoped - that is what makes them cross-tenant.
            TenantId = model.Role == AppRoles.SuperAdmin ? null : model.TenantId
        };

        var created = await _users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            foreach (var e in created.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(await BuildCreateModelAsync(model));
        }

        await _users.AddToRoleAsync(user, model.Role);

        _log.LogInformation("User {Email} created with role {Role} on tenant {TenantId}.",
            user.Email, model.Role, user.TenantId);

        TempData["Success"] =
            $"{user.FullName} created. Give them the temporary password directly - it is not emailed.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(Guid id)
    {
        await _tenant.LoadAsync();

        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        if (!_tenant.IsPlatformUser && user.TenantId != _tenant.TenantId)
            return Forbid();

        if (user.Id.ToString() == _users.GetUserId(User))
        {
            TempData["Error"] = "You cannot deactivate your own account.";
            return RedirectToAction(nameof(Index));
        }

        user.IsActive = !user.IsActive;
        await _users.UpdateAsync(user);

        // Ends sessions already open in other browsers at their next security check.
        if (!user.IsActive) await _users.UpdateSecurityStampAsync(user);

        TempData["Success"] = user.IsActive
            ? $"{user.FullName} reactivated."
            : $"{user.FullName} deactivated.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Replaces a user's password with a generated temporary one, shown once on the page and
    /// stored nowhere else. The user must change it at next sign-in, any lockout is lifted,
    /// and sessions they already have open end at the next security check.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid id)
    {
        ViewData["Title"] = "Password reset";
        await _tenant.LoadAsync();

        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        // A customer administrator reaches only their own people - which also keeps platform
        // staff (no tenant) out of their reach.
        if (!_tenant.IsPlatformUser && user.TenantId != _tenant.TenantId)
            return Forbid();

        if (user.Id.ToString() == _users.GetUserId(User))
        {
            TempData["Error"] = "Use Change password for your own account.";
            return RedirectToAction(nameof(Index));
        }

        var temporary = TemporaryPassword();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, temporary);

        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Index));
        }

        user.MustChangePassword = true;
        await _users.UpdateAsync(user);
        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);

        _log.LogInformation("Password reset for {Email} by {Admin}.", user.Email, User.Identity?.Name);

        // Rendered directly rather than redirected: the password never sits in TempData or a cookie,
        // and the page is not cached, so Back cannot bring it up again.
        Response.Headers.CacheControl = "no-store";
        return View("PasswordReset", new PasswordResetResult(user.FullName, user.Email ?? "", temporary));
    }

    /// <summary>16 characters from an unambiguous alphabet, always meeting the password policy.</summary>
    public static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz",
                     digits = "23456789", symbols = "!@#$%*?";
        var all = upper + lower + digits + symbols;

        var chars = new List<char>
        {
            upper[System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper.Length)],
            lower[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lower.Length)],
            digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[System.Security.Cryptography.RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (chars.Count < 16)
            chars.Add(all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)]);

        // Shuffle so the guaranteed classes are not always in the first four positions.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private async Task<CreateUserModel> BuildCreateModelAsync(CreateUserModel model)
    {
        model.Tenants = await _db.Tenants
            .AsNoTracking()
            .Where(t => _tenant.IsPlatformUser || t.Id == _tenant.TenantId)
            .OrderBy(t => t.Name)
            .Select(t => new ValueTuple<Guid, string>(t.Id, t.Name))
            .ToListAsync();

        return model;
    }
}

public record PasswordResetResult(string FullName, string Email, string TemporaryPassword);
