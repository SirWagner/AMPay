using AMPay.Domain.Enums;
using AMPay.Domain.Portal;
using AMPay.Domain.Validation;
using AMPay.Portal.Data;
using AMPay.Portal.Models;
using AMPay.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Portal.Controllers;

/// <summary>
/// The client's own application, after signing in with a code to their cell number.
/// Five short steps, then submit. Once submitted it is read-only: the lender takes it from there.
/// </summary>
[Route("a/{code}/application")]
[Authorize(AuthenticationSchemes = ApplicantClaims.Scheme)]
[EnableRateLimiting(PortalRateLimits.Pages)]
public class ApplicationController : PortalControllerBase
{
    private readonly EstimateService _estimates;
    private readonly PortalDocumentStore _store;
    private readonly PortalOptions _options;

    private Lender _lender = null!;
    private Guid _applicantId;

    public ApplicationController(PortalDbContext db, EstimateService estimates, PortalDocumentStore store, IOptions<PortalOptions> options)
        : base(db)
    {
        _estimates = estimates;
        _store = store;
        _options = options.Value;
    }

    /// <summary>Every action belongs to the lender in the URL and to the signed-in number.</summary>
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var code = context.RouteData.Values["code"]?.ToString() ?? "";
        var lender = await LenderAsync(code);

        if (lender is null)
        {
            context.Result = NotFoundPage();
            return;
        }

        // Signed in for another lender: that session is no good here.
        if (!SignedInFor(lender) || ApplicantId() is not { } applicantId)
        {
            await HttpContext.SignOutAsync(ApplicantClaims.Scheme);
            context.Result = RedirectToAction("Start", "Lender", new { code });
            return;
        }

        _lender = lender;
        _applicantId = applicantId;
        await base.OnActionExecutionAsync(context, next);
    }

    // ---------------------------------------------------------------- overview

    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] string code)
    {
        var app = await CurrentAsync(createIfNone: true);
        ViewBag.Missing = MissingFor(app!);
        return View(app);
    }

    // ---------------------------------------------------------------- about you

    [HttpGet("details")]
    public async Task<IActionResult> Details([FromRoute] string code)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        return View(new DetailsModel
        {
            Title = app.Title, FirstName = app.FirstName ?? "", MiddleNames = app.MiddleNames, Surname = app.Surname ?? "",
            IdNumber = app.IdNumber ?? "", Email = app.Email, AddressLine1 = app.AddressLine1 ?? "", AddressLine2 = app.AddressLine2,
            Suburb = app.Suburb, City = app.City ?? "", Province = app.Province, PostalCode = app.PostalCode
        });
    }

    [HttpPost("details")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Details([FromRoute] string code, DetailsModel model)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        var id = new string((model.IdNumber ?? "").Where(char.IsDigit).ToArray());
        if (!SaIdNumber.IsValid(id))
            ModelState.AddModelError(nameof(model.IdNumber), "That is not a valid South African ID number. Check the 13 digits.");

        if (!ModelState.IsValid) return View(model);

        app.Title = model.Title?.Trim();
        app.FirstName = model.FirstName.Trim();
        app.MiddleNames = model.MiddleNames?.Trim();
        app.Surname = model.Surname.Trim();
        app.IdNumber = id;
        app.Email = model.Email?.Trim();
        app.AddressLine1 = model.AddressLine1.Trim();
        app.AddressLine2 = model.AddressLine2?.Trim();
        app.Suburb = model.Suburb?.Trim();
        app.City = model.City.Trim();
        app.Province = model.Province?.Trim();
        app.PostalCode = model.PostalCode?.Trim();
        app.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Income), new { code });
    }

    // ---------------------------------------------------------------- work and income

    [HttpGet("income")]
    public async Task<IActionResult> Income([FromRoute] string code)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        return View(new IncomeModel
        {
            EmployerName = app.EmployerName ?? "", Occupation = app.Occupation, EmployedSince = app.EmployedSince,
            PayFrequency = app.PayFrequency ?? "Monthly", SalaryDay = app.SalaryDay,
            GrossMonthlyIncome = app.GrossMonthlyIncome, NetMonthlyIncome = app.NetMonthlyIncome,
            OtherMonthlyIncome = app.OtherMonthlyIncome, MonthlyExpenses = app.MonthlyExpenses,
            MonthlyDebtRepayments = app.MonthlyDebtRepayments
        });
    }

    [HttpPost("income")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Income([FromRoute] string code, IncomeModel model)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        if (model.NetMonthlyIncome > model.GrossMonthlyIncome)
            ModelState.AddModelError(nameof(model.NetMonthlyIncome), "Take-home pay cannot be more than your gross salary.");
        if (model.EmployedSince > DateTime.Today)
            ModelState.AddModelError(nameof(model.EmployedSince), "That date is in the future.");

        if (!ModelState.IsValid) return View(model);

        app.EmployerName = model.EmployerName.Trim();
        app.Occupation = model.Occupation?.Trim();
        app.EmployedSince = model.EmployedSince;
        app.PayFrequency = model.PayFrequency;
        app.SalaryDay = model.SalaryDay;
        app.GrossMonthlyIncome = model.GrossMonthlyIncome;
        app.NetMonthlyIncome = model.NetMonthlyIncome;
        app.OtherMonthlyIncome = model.OtherMonthlyIncome;
        app.MonthlyExpenses = model.MonthlyExpenses;
        app.MonthlyDebtRepayments = model.MonthlyDebtRepayments;
        app.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Loan), new { code });
    }

    // ---------------------------------------------------------------- the loan

    [HttpGet("loan")]
    public async Task<IActionResult> Loan([FromRoute] string code)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        var model = new LoanModel { Amount = app.RequestedAmount, Term = app.RequestedTerm, Range = EstimateService.RangeOf(_lender) };
        if (model.Amount is { } a && model.Term is { } t) model.Estimate = _estimates.For(_lender, a, t, app.SalaryDay);
        return View(model);
    }

    [HttpPost("loan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Loan([FromRoute] string code, LoanModel model, string? command)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        model.Range = EstimateService.RangeOf(_lender);
        if (model.Range is null)
            ModelState.AddModelError("", $"{_lender.DisplayName} is not taking applications online yet. Use Talk to an advisor instead.");

        if (ModelState.IsValid && model.Amount is { } amount && model.Term is { } term)
        {
            model.Estimate = _estimates.For(_lender, amount, term, app.SalaryDay);
            if (model.Estimate is null)
                ModelState.AddModelError(nameof(model.Amount),
                    $"{_lender.DisplayName} lends {Money(model.Range!.MinAmount)} to {Money(model.Range.MaxAmount)} " +
                    $"over {model.Range.MinTerm} to {model.Range.MaxTerm} months. Not every amount is available over every term.");
        }

        // "See the estimate" redraws the page; only "Continue" saves and moves on.
        if (!ModelState.IsValid || command == "estimate") return View(model);

        app.RequestedAmount = model.Amount;
        app.RequestedTerm = model.Term;
        app.PackageName = model.Estimate!.PackageName;
        app.EstimatedInstalment = model.Estimate.Instalment;
        app.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Documents), new { code });
    }

    // ---------------------------------------------------------------- documents

    [HttpGet("documents")]
    public async Task<IActionResult> Documents([FromRoute] string code)
    {
        var app = await CurrentAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });
        return View(new DocumentsModel { Application = app, MaxFileSizeBytes = _options.MaxFileSizeBytes });
    }

    [HttpPost("documents")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromRoute] string code, DocumentType documentType, IFormFile? file)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        if (!DocumentsModel.Kinds.Any(k => k.Type == documentType))
            return BadRequest();

        if (file is null)
        {
            TempData["Error"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Documents), new { code });
        }

        var (stored, error) = await _store.SaveAsync(_lender.Id, app.Id, file);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Documents), new { code });
        }

        Db.Documents.Add(new ApplicationDocument
        {
            ApplicationId = app.Id,
            DocumentType = documentType,
            FileName = Path.GetFileName(file.FileName),
            ContentType = stored!.ContentType,
            SizeBytes = stored.SizeBytes,
            StoragePath = stored.StoragePath,
            Sha256 = stored.Sha256
        });
        app.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        TempData["Success"] = $"{DocumentsModel.Kinds.First(k => k.Type == documentType).Label} uploaded.";
        return RedirectToAction(nameof(Documents), new { code });
    }

    [HttpPost("documents/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDocument([FromRoute] string code, Guid documentId)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        var doc = app.Documents.FirstOrDefault(d => d.Id == documentId);
        if (doc is not null)
        {
            _store.Delete(doc.StoragePath);
            Db.Documents.Remove(doc);
            await Db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Documents), new { code });
    }

    // ---------------------------------------------------------------- review and submit

    [HttpGet("review")]
    public async Task<IActionResult> Review([FromRoute] string code)
    {
        var app = await CurrentAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });
        return View(new ReviewModel { Application = app, Missing = MissingFor(app) });
    }

    [HttpPost("submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit([FromRoute] string code, ReviewModel posted)
    {
        var app = await EditableAsync();
        if (app is null) return RedirectToAction(nameof(Index), new { code });

        var missing = MissingFor(app).ToList();
        if (!posted.DataProcessingConsent) missing.Add("your consent to process your information");
        if (!posted.CreditCheckConsent) missing.Add("your consent to a credit check");

        if (missing.Count > 0)
        {
            ModelState.AddModelError("", "Before you submit, we still need: " + string.Join(", ", missing) + ".");
            return View("Review", new ReviewModel
            {
                Application = app, Missing = MissingFor(app),
                DataProcessingConsent = posted.DataProcessingConsent, CreditCheckConsent = posted.CreditCheckConsent,
                MarketingConsent = posted.MarketingConsent
            });
        }

        var now = DateTime.UtcNow;
        app.DataProcessingConsent = true;
        app.CreditCheckConsent = true;
        app.MarketingConsent = posted.MarketingConsent;
        app.ConsentUtc = now;
        app.ConsentIp = ClientIp();
        app.Status = PortalApplicationStatus.Submitted;
        app.SubmittedUtc = now;
        app.UpdatedUtc = now;
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { code });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The client's latest application with this lender; a new draft when they have none open.</summary>
    private async Task<LoanApplication?> CurrentAsync(bool createIfNone = false)
    {
        var app = await Db.Applications
            .Include(a => a.Documents)
            .Where(a => a.ApplicantId == _applicantId && a.LenderId == _lender.Id && a.Status != PortalApplicationStatus.Withdrawn)
            .OrderByDescending(a => a.CreatedUtc)
            .FirstOrDefaultAsync();

        if (app is not null || !createIfNone) return app;

        app = new LoanApplication { LenderId = _lender.Id, ApplicantId = _applicantId, Reference = NewReference() };
        Db.Applications.Add(app);
        await Db.SaveChangesAsync();
        return app;
    }

    private async Task<LoanApplication?> EditableAsync()
    {
        var app = await CurrentAsync(createIfNone: true);
        return app is { IsEditable: true } ? app : null;
    }

    /// <summary>What still has to be filled in before the application can go to the lender.</summary>
    public static List<string> MissingFor(LoanApplication a)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(a.IdNumber) || string.IsNullOrWhiteSpace(a.AddressLine1)) missing.Add("your details");
        if (a.NetMonthlyIncome is null || string.IsNullOrWhiteSpace(a.EmployerName)) missing.Add("your work and income");
        if (a.RequestedAmount is null || a.RequestedTerm is null) missing.Add("the loan you need");
        foreach (var kind in DocumentsModel.Kinds.Where(k => k.Required))
            if (!a.Documents.Any(d => d.DocumentType == kind.Type)) missing.Add($"your {kind.Label.ToLowerInvariant()}");
        return missing;
    }

    private static string NewReference() => "APP-" + PortalApi.NewLenderCode();

    internal static string Money(decimal v) => "R " + v.ToString("N0");
}
