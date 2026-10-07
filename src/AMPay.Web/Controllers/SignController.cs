using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
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
/// The client's side of a contract pack, reached by the link sent by SMS or email.
/// <para>
/// No account and no password: the link is the key to reading, and a PIN sent to the cell
/// number on the agreement plus the client's ID number is the key to signing. The link is
/// random, stored only as a hash, expires, and dies the moment a new one is sent or the
/// pack is voided. Every request is rate limited per address.
/// </para>
/// </summary>
[AllowAnonymous]
[Route("sign/{token}")]
[EnableRateLimiting(RateLimits.PublicPages)]
public class SignController : Controller
{
    private readonly AppDbContext _db;
    private readonly ContractService _contracts;

    public SignController(AppDbContext db, ContractService contracts)
    {
        _db = db;
        _contracts = contracts;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // The token is in the path: never let it leave in a Referer header, a cache or a search index.
        Response.Headers["Referrer-Policy"] = "no-referrer";
        // The values antiforgery itself requires, so it has nothing to override.
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        base.OnActionExecuting(context);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string token)
    {
        var contract = await _contracts.FindByTokenAsync(token);
        if (contract is null) return Gone();

        await _contracts.RecordViewAsync(contract);
        return View(await PageAsync(token, contract, TempData["SignError"] as string, TempData["SignNotice"] as string));
    }

    [HttpPost("pin")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.SigningPin)]
    public async Task<IActionResult> RequestPin(string token)
    {
        var contract = await _contracts.FindByTokenAsync(token);
        if (contract is null) return Gone();

        try
        {
            var masked = await _contracts.SendPinAsync(contract);
            TempData["SignNotice"] = $"A PIN has been sent to {masked}. It expires in 10 minutes.";
        }
        catch (ContractException ex)
        {
            TempData["SignError"] = ex.Message;
        }

        return Redirect($"/sign/{Uri.EscapeDataString(token)}#sign");
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.SigningPin)]
    public async Task<IActionResult> Sign(string token, string? pin, string? fullName, string? idNumber, bool accepted)
    {
        var contract = await _contracts.FindByTokenAsync(token);
        if (contract is null) return Gone();

        try
        {
            await _contracts.SignOnlineAsync(
                contract, pin, fullName, idNumber, accepted,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString());

            TempData["SignNotice"] = "Thank you - your agreement is signed. Download your signed copy below and keep it.";
        }
        catch (ContractException ex)
        {
            TempData["SignError"] = ex.Message;
        }

        return Redirect($"/sign/{Uri.EscapeDataString(token)}#sign");
    }

    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf(string token)
    {
        var contract = await _contracts.FindByTokenAsync(token);
        if (contract is null) return Gone();

        var bytes = _contracts.RenderPublicPdf(contract);
        return File(bytes, "application/pdf", ContractsController.FileNameFor(contract));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<SignPageModel> PageAsync(string token, LoanContract c, string? error, string? notice)
    {
        var snapshot = ContractService.ReadSnapshot(c);
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == c.TenantId);

        ViewData["Title"] = $"Agreement {c.Reference}";
        ViewData["Lender"] = tenant.TradingName ?? tenant.Name;

        return new SignPageModel
        {
            Token = token,
            Reference = c.Reference,
            Status = c.Status,
            Document = new ContractDocumentView(snapshot.ForPublicView(), c.SnapshotHash, ContractService.PublicSignatureOf(c)),
            Lender = tenant.TradingName ?? tenant.Name,
            LenderContact = tenant.ContactNumber,
            CanSignOnline = c.IsOpen && !string.IsNullOrWhiteSpace(snapshot.Borrower.Mobile),
            PinSent = c.OtpHash is not null && c.OtpExpiresUtc > DateTime.UtcNow,
            MaskedMobile = ContractFormat.MaskMobile(snapshot.Borrower.Mobile),
            Stubbed = _contracts.MessagesAreStubbed,
            Error = error,
            Notice = notice
        };
    }

    /// <summary>One answer for wrong, expired and withdrawn links: which it was helps nobody but a guesser.</summary>
    private IActionResult Gone()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        ViewData["Title"] = "Link not valid";
        return View("Gone");
    }
}

public static class RateLimits
{
    /// <summary>Reading public pages: generous, per address.</summary>
    public const string PublicPages = "public-pages";

    /// <summary>Asking for a PIN and submitting one: tight, per address.</summary>
    public const string SigningPin = "signing-pin";
}
