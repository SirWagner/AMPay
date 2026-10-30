using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Controllers;

/// <summary>
/// Quotes from the lender's side, all from the quote card on the loan page: issue, send,
/// record an in-branch acceptance, void. The client answers through <see cref="QuoteLinkController"/>.
/// </summary>
[Authorize]
public class QuotesController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly QuoteService _quotes;
    private readonly UserManager<ApplicationUser> _users;
    private readonly MessagingOptions _messaging;

    public QuotesController(
        AppDbContext db,
        ICurrentTenant tenant,
        QuoteService quotes,
        UserManager<ApplicationUser> users,
        IOptions<MessagingOptions> messaging)
    {
        _db = db;
        _tenant = tenant;
        _quotes = quotes;
        _users = users;
        _messaging = messaging.Value;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Issue(Guid loanId)
    {
        var tenantId = await _db.Loans.Where(l => l.Id == loanId).Select(l => (Guid?)l.TenantId).FirstOrDefaultAsync();
        if (tenantId is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(tenantId.Value);

        try
        {
            var quote = await _quotes.IssueAsync(loanId, _users.GetUserId(User));
            TempData["Success"] = $"Quote {quote.Reference} issued. Send it to the client, or record their acceptance in the branch.";
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return BackToLoan(loanId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Send(Guid id, bool sms, bool email)
    {
        var loanId = await LoanOfAsync(id);
        if (loanId is null) return NotFound();

        try
        {
            var outcome = await _quotes.SendAsync(id, sms, email, PublicBaseUrl(), _users.GetUserId(User));

            var sent = string.Join(" and ", outcome.SentTo);
            var skipped = outcome.Skipped.Count == 0 ? "" : $" Not sent: {string.Join("; ", outcome.Skipped)}.";

            TempData["Success"] = outcome.Stubbed
                ? $"Recorded for sending by {sent} - no SMS or email provider is connected yet, so nothing " +
                  $"reached the client. The messages are under Messages sent.{skipped}"
                : $"Sent by {sent}.{skipped}";

            // Shown once, so it can be copied into WhatsApp or read out. Only its hash is stored.
            TempData["QuoteLink"] = outcome.Link;
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return BackToLoan(loanId.Value);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> AcceptInBranch(Guid id, int[]? consent)
    {
        var loanId = await LoanOfAsync(id);
        if (loanId is null) return NotFound();

        try
        {
            await _quotes.RecordInBranchAsync(id, consent ?? Array.Empty<int>(), _users.GetUserId(User));
            TempData["Success"] = "Quote recorded as accepted in the branch. You can now issue the credit agreement.";
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return BackToLoan(loanId.Value);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanApproveCredit)]
    public async Task<IActionResult> Void(Guid id, string? reason)
    {
        var loanId = await LoanOfAsync(id);
        if (loanId is null) return NotFound();

        try
        {
            await _quotes.VoidAsync(id, reason, _users.GetUserId(User));
            TempData["Success"] = "Quote voided. Its link no longer works.";
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return BackToLoan(loanId.Value);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The quote's loan, after checking the quote belongs to a business this user may act for.</summary>
    private async Task<Guid?> LoanOfAsync(Guid quoteId)
    {
        var q = await _db.CreditQuotes.AsNoTracking()
            .Where(x => x.Id == quoteId)
            .Select(x => new { x.LoanId, x.TenantId })
            .FirstOrDefaultAsync();
        if (q is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(q.TenantId);
        return q.LoanId;
    }

    private IActionResult BackToLoan(Guid loanId) =>
        Redirect(Url.Action("Details", "Loans", new { id = loanId }) + "#quote");

    private string PublicBaseUrl() =>
        string.IsNullOrWhiteSpace(_messaging.PublicBaseUrl)
            ? $"{Request.Scheme}://{Request.Host}"
            : _messaging.PublicBaseUrl;
}
