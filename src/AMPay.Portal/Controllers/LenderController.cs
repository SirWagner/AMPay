using System.Security.Claims;
using AMPay.Domain.Portal;
using AMPay.Portal.Data;
using AMPay.Portal.Models;
using AMPay.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Portal.Controllers;

/// <summary>
/// The public face of one lender: /a/{code}. Anyone can reach it - it is the page behind the
/// "Apply now" button on the lender's own website.
/// </summary>
[Route("a/{code}")]
[EnableRateLimiting(PortalRateLimits.Pages)]
public class LenderController : PortalControllerBase
{
    private readonly OtpService _otp;

    public LenderController(PortalDbContext db, OtpService otp) : base(db) => _otp = otp;

    [HttpGet("")]
    public async Task<IActionResult> Landing([FromRoute] string code)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();

        return View(new LandingModel
        {
            Lender = lender,
            SignedIn = SignedInFor(lender),
            Range = EstimateService.RangeOf(lender)
        });
    }

    // ---------------------------------------------------------------- sign in

    [HttpGet("start")]
    public async Task<IActionResult> Start([FromRoute] string code)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();
        if (SignedInFor(lender)) return RedirectToAction("Index", "Application", new { code });

        return View(new StartModel());
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PortalRateLimits.Codes)]
    public async Task<IActionResult> Start([FromRoute] string code, StartModel model)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();

        var mobile = PortalApi.NormaliseMobile(model.Mobile);
        if (mobile is null)
            ModelState.AddModelError(nameof(model.Mobile), "Enter a South African cell number, like 082 123 4567.");

        if (!ModelState.IsValid) return View(model);

        var (issued, error) = await _otp.IssueAsync(lender, mobile!, ClientIp());
        if (error is not null)
        {
            ModelState.AddModelError(nameof(model.Mobile), error);
            return View(model);
        }

        if (issued!.TestCode is not null) TempData["TestCode"] = issued.TestCode;
        TempData["Mobile"] = PortalApi.DisplayMobile(mobile!);

        return RedirectToAction(nameof(Verify), new { code, c = issued.ChallengeId });
    }

    [HttpGet("verify")]
    public async Task<IActionResult> Verify([FromRoute] string code, Guid c)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();

        return View(new VerifyModel
        {
            ChallengeId = c,
            MaskedMobile = TempData["Mobile"] as string ?? "your phone",
            TestCode = TempData["TestCode"] as string
        });
    }

    [HttpPost("verify")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PortalRateLimits.Codes)]
    public async Task<IActionResult> Verify([FromRoute] string code, VerifyModel model)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();
        if (!ModelState.IsValid) return View(model);

        var (mobile, error) = await _otp.VerifyAsync(model.ChallengeId, lender.Id, model.Code);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(model.Code), error);
            return View(model);
        }

        var applicant = await Db.Applicants.FirstOrDefaultAsync(a => a.LenderId == lender.Id && a.Mobile == mobile);
        if (applicant is null)
        {
            applicant = new Applicant { LenderId = lender.Id, Mobile = mobile! };
            Db.Applicants.Add(applicant);
        }
        applicant.LastSignInUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ApplicantClaims.ApplicantId, applicant.Id.ToString()),
            new Claim(ApplicantClaims.LenderId, lender.Id.ToString()),
            new Claim(ApplicantClaims.Mobile, applicant.Mobile)
        }, ApplicantClaims.Scheme);

        await HttpContext.SignInAsync(ApplicantClaims.Scheme, new ClaimsPrincipal(identity));
        return RedirectToAction("Index", "Application", new { code });
    }

    [HttpPost("signout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutApplicant([FromRoute] string code)
    {
        await HttpContext.SignOutAsync(ApplicantClaims.Scheme);
        return RedirectToAction(nameof(Landing), new { code });
    }

    // ---------------------------------------------------------------- talk to an advisor

    [HttpGet("advisor")]
    public async Task<IActionResult> Advisor([FromRoute] string code)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();

        ViewBag.Lender = lender;
        var model = new AdvisorModel();
        if (User.FindFirst(ApplicantClaims.Mobile)?.Value is { } m && SignedInFor(lender))
            model.Mobile = PortalApi.DisplayMobile(m);
        return View(model);
    }

    [HttpPost("advisor")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PortalRateLimits.Codes)]
    public async Task<IActionResult> Advisor([FromRoute] string code, AdvisorModel model)
    {
        var lender = await LenderAsync(code);
        if (lender is null) return NotFoundPage();
        ViewBag.Lender = lender;

        // A filled honeypot is a bot. Pretend it worked; store nothing.
        if (!string.IsNullOrEmpty(model.Website)) return View("AdvisorDone", lender);

        var mobile = PortalApi.NormaliseMobile(model.Mobile);
        if (mobile is null)
            ModelState.AddModelError(nameof(model.Mobile), "Enter a South African cell number, like 082 123 4567.");
        if (!ModelState.IsValid) return View(model);

        // One open request per number is plenty; a second press just updates it.
        var open = await Db.Callbacks.FirstOrDefaultAsync(c => c.LenderId == lender.Id && c.Mobile == mobile && c.HandledUtc == null);
        if (open is null)
        {
            open = new CallbackRequest { LenderId = lender.Id, Mobile = mobile! };
            Db.Callbacks.Add(open);
        }

        open.Name = model.Name.Trim();
        open.PreferredTime = model.PreferredTime?.Trim();
        open.Message = model.Message?.Trim();
        open.RequestIp = ClientIp();
        open.ApplicationId = await CurrentApplicationIdAsync(lender);
        await Db.SaveChangesAsync();

        return View("AdvisorDone", lender);
    }

    private async Task<Guid?> CurrentApplicationIdAsync(Lender lender)
    {
        if (!SignedInFor(lender) || ApplicantId() is not { } applicantId) return null;
        return await Db.Applications
            .Where(a => a.ApplicantId == applicantId && a.Status != PortalApplicationStatus.Withdrawn)
            .OrderByDescending(a => a.CreatedUtc)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }
}

/// <summary>Shared lookups for the public controllers.</summary>
public abstract class PortalControllerBase : Controller
{
    protected readonly PortalDbContext Db;

    protected PortalControllerBase(PortalDbContext db) => Db = db;

    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        // Personal details on every page: never cache, never index, never leak the path.
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "same-origin";
        base.OnActionExecuting(context);
    }

    protected async Task<Lender?> LenderAsync([FromRoute] string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (!PortalApi.IsValidLenderCode(code)) return null;

        var lender = await Db.Lenders.AsNoTracking().FirstOrDefaultAsync(l => l.PublicCode == code && l.IsActive);
        if (lender is not null) ViewData["Lender"] = lender;
        return lender;
    }

    protected bool SignedInFor(Lender lender) =>
        User.Identity?.IsAuthenticated == true &&
        User.FindFirst(ApplicantClaims.LenderId)?.Value == lender.Id.ToString();

    protected Guid? ApplicantId() =>
        Guid.TryParse(User.FindFirst(ApplicantClaims.ApplicantId)?.Value, out var id) ? id : null;

    protected string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    protected IActionResult NotFoundPage()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View("~/Views/Shared/LenderNotFound.cshtml");
    }
}

public static class PortalRateLimits
{
    public const string Pages = "portal-pages";
    public const string Codes = "portal-codes";
    public const string Api = "portal-api";
}
