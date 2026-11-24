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
            context.Result = RedirectToAction("Start", "Lender", new { code, to = "account" });
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
            ViewBag.Request = await Db.LinkRequests.AsNoTracking()
                .Where(r => r.ApplicantId == _applicant.Id)
                .OrderByDescending(r => r.CreatedUtc)
                .FirstOrDefaultAsync();
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

    /// <summary>"I am already a client": the lender confirms who this is before anything is shown.</summary>
    [HttpPost("claim")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PortalRateLimits.Codes)]
    public async Task<IActionResult> Claim([FromRoute] string code, string? idNumber, string? email, string? fullName)
    {
        var id = new string((idNumber ?? "").Where(char.IsDigit).ToArray());
        if (!AMPay.Domain.Validation.SaIdNumber.IsValid(id))
        {
            TempData["Error"] = "Enter your 13-digit South African ID number.";
            return RedirectToAction(nameof(Index), new { code });
        }
        email = email?.Trim();
        if (!string.IsNullOrEmpty(email) && (email.Length > 254 || !email.Contains('@')))
        {
            TempData["Error"] = "That email address does not look right.";
            return RedirectToAction(nameof(Index), new { code });
        }

        var pending = await Db.LinkRequests.FirstOrDefaultAsync(r => r.ApplicantId == _applicant.Id && r.Status == LinkRequestStatus.Pending);
        if (pending is null)
        {
            pending = new AccountLinkRequest { LenderId = _lender.Id, ApplicantId = _applicant.Id, Mobile = _applicant.Mobile };
            Db.LinkRequests.Add(pending);
        }
        pending.Email = email?.ToLowerInvariant() ?? "";
        pending.IdNumber = id;
        var name = fullName?.Trim();
        pending.FullName = string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, 200)];
        pending.CreatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        TempData["Success"] = "Client already exists - wait for login validation. " + _lender.DisplayName + " will confirm it is you.";
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

    // ---------------------------------------------------------------- documents

    [HttpGet("document/{key}")]
    public async Task<IActionResult> Document([FromRoute] string code, string key)
    {
        var account = await LinkedAccountAsync();
        if (account is null) return RedirectToAction(nameof(Index), new { code });

        var doc = await Db.AccountDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ClientAccountId == account.Id && d.Key == key);
        if (doc is null) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(doc.Data, doc.ContentType, $"{doc.Title}.pdf".Replace('/', '-'));
    }

    /// <summary>A statement of account for one loan over a period, ready to print or save as PDF.</summary>
    [HttpGet("loan/{loanNumber}/statement")]
    public async Task<IActionResult> Statement([FromRoute] string code, string loanNumber, DateTime? from, DateTime? to)
    {
        var account = await LinkedAccountAsync();
        if (account is null) return RedirectToAction(nameof(Index), new { code });

        var statement = Read(account);
        var loan = statement.Loans.FirstOrDefault(l => string.Equals(l.LoanNumber, loanNumber, StringComparison.OrdinalIgnoreCase));
        if (loan is null) return RedirectToAction(nameof(Index), new { code });

        var start = (from ?? loan.DisbursedUtc?.AddHours(2).Date ?? loan.AgreementDate ?? account.GeneratedUtc.AddMonths(-3)).Date;
        var end = (to ?? account.GeneratedUtc.AddHours(2)).Date;
        if (end < start) (start, end) = (end, start);

        ViewData["Title"] = $"Statement {loan.LoanNumber}";
        return View(new StatementModel { Statement = statement, Loan = loan, From = start, To = end, UpdatedUtc = account.GeneratedUtc });
    }

    /// <summary>The paid-up letter for a loan paid in full.</summary>
    [HttpGet("loan/{loanNumber}/paid-up")]
    public async Task<IActionResult> PaidUp([FromRoute] string code, string loanNumber)
    {
        var account = await LinkedAccountAsync();
        if (account is null) return RedirectToAction(nameof(Index), new { code });

        var statement = Read(account);
        var loan = statement.Loans.FirstOrDefault(l => string.Equals(l.LoanNumber, loanNumber, StringComparison.OrdinalIgnoreCase) && l.Status == "Paid up");
        if (loan is null) return RedirectToAction(nameof(Index), new { code });

        ViewData["Title"] = $"Paid-up letter {loan.LoanNumber}";
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
        // An approved request ties a number the lender does not hold on file; otherwise the numbers must agree.
        var approved = _applicant.LinkApprovedByLender;
        return await Db.ClientAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.LenderId == _lender.Id && (approved || a.Mobile == _applicant.Mobile));
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
