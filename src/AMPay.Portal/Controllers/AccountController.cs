using System.Text.Json;
using AMPay.Domain.Portal;
using AMPay.Portal.Data;
using AMPay.Portal.Models;
using AMPay.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Portal.Controllers;

/// <summary>
/// The client's own account with the lender: every loan, every cost, every instalment and
/// every payment - failed debits included. Reached from the lender's link, after the code to
/// the client's cell number, and tied to the client once by their ID number.
/// </summary>
[Route("a/{code}/account")]
[Authorize(AuthenticationSchemes = ApplicantClaims.Scheme)]
[EnableRateLimiting(PortalRateLimits.Pages)]
public class AccountController : PortalControllerBase
{
    private Lender _lender = null!;
    private Applicant _applicant = null!;

    public AccountController(PortalDbContext db) : base(db) { }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var code = context.RouteData.Values["code"]?.ToString() ?? "";
        var lender = await LenderAsync(code);
        if (lender is null)
        {
            context.Result = NotFoundPage();
            return;
        }

        var applicant = SignedInFor(lender) && ApplicantId() is { } id
            ? await Db.Applicants.FirstOrDefaultAsync(a => a.Id == id && a.LenderId == lender.Id)
            : null;
        if (applicant is null)
        {
            await HttpContext.SignOutAsync(ApplicantClaims.Scheme);
            context.Result = RedirectToAction("Start", "Lender", new { code });
            return;
        }

        _lender = lender;
        _applicant = applicant;
        await base.OnActionExecutionAsync(context, next);
    }

    // ---------------------------------------------------------------- overview

    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] string code)
    {
        var account = await LinkedAccountAsync();
        if (account is not null)
        {
            ViewData["Title"] = "My loans";
            return View(new AccountOverviewModel { Statement = Read(account), UpdatedUtc = account.GeneratedUtc });
        }

        if (!await HasAccountForNumberAsync())
        {
            ViewData["Title"] = "My loans";
            return View("NoAccount");
        }

        return ConfirmView(new ConfirmIdModel());
    }

    // ---------------------------------------------------------------- tying the number to the client

    [HttpPost("confirm")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PortalRateLimits.Codes)]
    public async Task<IActionResult> Confirm([FromRoute] string code, ConfirmIdModel model)
    {
        if (_applicant.FailedIdAttempts >= Applicant.MaxIdAttempts) return ConfirmView(model);
        if (!ModelState.IsValid) return ConfirmView(model);

        var hash = ClientStatementBuilder.IdNumberHash(_lender.AmpayTenantId, model.IdNumber);
        var match = await Db.ClientAccounts.FirstOrDefaultAsync(a =>
            a.LenderId == _lender.Id && a.Mobile == _applicant.Mobile && a.IdNumberHash == hash);

        if (match is null)
        {
            _applicant.FailedIdAttempts++;
            await Db.SaveChangesAsync();
            ModelState.AddModelError(nameof(model.IdNumber),
                _applicant.FailedIdAttempts >= Applicant.MaxIdAttempts
                    ? "That ID number does not match. Your account view is now locked - please contact the lender."
                    : "That ID number does not match the one the lender holds for this cell number.");
            return ConfirmView(model);
        }

        _applicant.ClientAccountId = match.Id;
        _applicant.AccountLinkedUtc = DateTime.UtcNow;
        _applicant.FailedIdAttempts = 0;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { code });
    }

    // ---------------------------------------------------------------- one loan

    [HttpGet("loan/{loanNumber}")]
    public async Task<IActionResult> Loan([FromRoute] string code, string loanNumber)
    {
        var account = await LinkedAccountAsync();
        if (account is null) return RedirectToAction(nameof(Index), new { code });

        var statement = Read(account);
        var loan = statement.Loans.FirstOrDefault(l => string.Equals(l.LoanNumber, loanNumber, StringComparison.OrdinalIgnoreCase));
        if (loan is null) return RedirectToAction(nameof(Index), new { code });

        ViewData["Title"] = $"Loan {loan.LoanNumber}";
        return View(new LoanDetailModel { Statement = statement, Loan = loan, UpdatedUtc = account.GeneratedUtc });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The account this person is tied to - but only while the lender still has this cell
    /// number on it. A number the lender has since changed loses the view at once.
    /// </summary>
    private async Task<ClientAccount?> LinkedAccountAsync()
    {
        if (_applicant.ClientAccountId is not { } id) return null;
        return await Db.ClientAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.LenderId == _lender.Id && a.Mobile == _applicant.Mobile);
    }

    private Task<bool> HasAccountForNumberAsync() =>
        Db.ClientAccounts.AnyAsync(a => a.LenderId == _lender.Id && a.Mobile == _applicant.Mobile);

    private IActionResult ConfirmView(ConfirmIdModel model)
    {
        ViewData["Title"] = "Confirm it is you";
        model.AttemptsLeft = Math.Max(0, Applicant.MaxIdAttempts - _applicant.FailedIdAttempts);
        return View("Confirm", model);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PortalClientStatement Read(ClientAccount account) =>
        JsonSerializer.Deserialize<PortalClientStatement>(account.StatementJson, Json)
        ?? throw new InvalidOperationException("The account statement could not be read.");
}
