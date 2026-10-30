using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The client's side of a quote, reached by the link sent by SMS or email. The client reads
/// the cost of credit and accepts it - three consents and a PIN sent to the cell number on
/// the quote - or declines it with a reason. Same protections as <see cref="SignController"/>.
/// </summary>
[AllowAnonymous]
[Route("q/{token}")]
[EnableRateLimiting(RateLimits.PublicPages)]
public class QuoteLinkController : Controller
{
    private readonly AppDbContext _db;
    private readonly QuoteService _quotes;

    public QuoteLinkController(AppDbContext db, QuoteService quotes)
    {
        _db = db;
        _quotes = quotes;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        base.OnActionExecuting(context);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string token)
    {
        var quote = await _quotes.FindByTokenAsync(token);
        if (quote is null) return Gone();

        await _quotes.RecordViewAsync(quote);
        return View(await PageAsync(token, quote, TempData["QuoteError"] as string, TempData["QuoteNotice"] as string));
    }

    [HttpPost("pin")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.SigningPin)]
    public async Task<IActionResult> RequestPin(string token)
    {
        var quote = await _quotes.FindByTokenAsync(token);
        if (quote is null) return Gone();

        try
        {
            var masked = await _quotes.SendPinAsync(quote);
            TempData["QuoteNotice"] = $"A PIN has been sent to {masked}. It expires in 5 minutes.";
        }
        catch (ContractException ex)
        {
            TempData["QuoteError"] = ex.Message;
        }

        return Back(token);
    }

    [HttpPost("accept")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.SigningPin)]
    public async Task<IActionResult> Accept(string token, string? pin, int[]? consent)
    {
        var quote = await _quotes.FindByTokenAsync(token);
        if (quote is null) return Gone();

        try
        {
            await _quotes.AcceptOnlineAsync(
                quote, pin, consent ?? Array.Empty<int>(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString());

            TempData["QuoteNotice"] = "Thank you - you have accepted the quote. Your credit agreement will be sent to you to sign.";
        }
        catch (ContractException ex)
        {
            TempData["QuoteError"] = ex.Message;
        }

        return Back(token);
    }

    [HttpPost("decline")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.SigningPin)]
    public async Task<IActionResult> Decline(string token, string? reason, string? detail)
    {
        var quote = await _quotes.FindByTokenAsync(token);
        if (quote is null) return Gone();

        try
        {
            await _quotes.DeclineAsync(quote, reason, detail);
            TempData["QuoteNotice"] = "You have declined the quote. Nothing further will happen with this loan.";
        }
        catch (ContractException ex)
        {
            TempData["QuoteError"] = ex.Message;
        }

        return Back(token);
    }

    // ---------------------------------------------------------------- helpers

    private IActionResult Back(string token) => Redirect($"/q/{Uri.EscapeDataString(token)}#answer");

    private async Task<QuotePageModel> PageAsync(string token, CreditQuote q, string? error, string? notice)
    {
        var snapshot = QuoteService.ReadSnapshot(q).ForPublicView();
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == q.TenantId);
        var lender = tenant.TradingName ?? tenant.Name;

        ViewData["Title"] = $"Quote {q.Reference}";
        ViewData["Lender"] = lender;

        return new QuotePageModel
        {
            Token = token,
            Quote = q,
            Snapshot = snapshot,
            Lender = lender,
            LenderContact = tenant.ContactNumber,
            Consents = QuoteService.Consents(lender),
            CanAcceptOnline = q.IsOpen && !string.IsNullOrWhiteSpace(snapshot.Borrower.Mobile),
            PinSent = q.OtpHash is not null && q.OtpExpiresUtc > DateTime.UtcNow,
            MaskedMobile = ContractFormat.MaskMobile(snapshot.Borrower.Mobile),
            Stubbed = _quotes.MessagesAreStubbed,
            Error = error,
            Notice = notice
        };
    }

    private IActionResult Gone()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        ViewData["Title"] = "Link not valid";
        return View("Gone");
    }
}
