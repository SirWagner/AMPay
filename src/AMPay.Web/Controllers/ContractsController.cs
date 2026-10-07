using AMPay.Domain.Entities;
using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Controllers;

/// <summary>
/// Contract packs from the lender's side: issue, read, download, send with the mandate,
/// record a paper signature, void.
/// <para>
/// A pack is a frozen snapshot of the approved loan - quote, schedule, budget, credit life,
/// debit order mandate and the lender's wording. The client reads and signs the same
/// snapshot through <see cref="SignController"/>.
/// </para>
/// </summary>
[Authorize]
public class ContractsController : Controller
{
    private const string ReadRoles =
        AppRoles.SuperAdmin + "," + AppRoles.TenantAdmin + "," + AppRoles.Capturer + "," + AppRoles.Viewer;

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly ContractService _contracts;
    private readonly UserManager<ApplicationUser> _users;
    private readonly MessagingOptions _messaging;

    public ContractsController(
        AppDbContext db,
        ICurrentTenant tenant,
        ContractService contracts,
        UserManager<ApplicationUser> users,
        IOptions<MessagingOptions> messaging)
    {
        _db = db;
        _tenant = tenant;
        _contracts = contracts;
        _users = users;
        _messaging = messaging.Value;
    }

    // ---------------------------------------------------------------- issue

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Issue(Guid loanId)
    {
        var loan = await _db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId);
        if (loan is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(loan.TenantId);

        try
        {
            var contract = await _contracts.IssueAsync(loanId, _users.GetUserId(User));
            TempData["Success"] = contract.TemplatesApproved
                ? $"Contract {contract.Reference} issued. Read it, then send it to the client."
                : $"Contract {contract.Reference} issued with draft wording - it carries a DRAFT mark until an " +
                  "administrator approves the contract wording.";
            return RedirectToAction(nameof(Details), new { id = contract.Id });
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction("Details", "Loans", new { id = loanId });
        }
    }

    // ---------------------------------------------------------------- read

    [Authorize(Roles = ReadRoles)]
    public async Task<IActionResult> Details(Guid id)
    {
        var contract = await LoadAsync(id);
        if (contract is null) return NotFound();

        var loanNumber = await _db.Loans.Where(l => l.Id == contract.LoanId).Select(l => l.LoanNumber).FirstAsync();

        var userIds = new[]
            {
                contract.CreatedByUserId, contract.SentByUserId,
                contract.SignedRecordedByUserId, contract.VoidedByUserId
            }
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        var names = await _db.Users
            .Where(u => userIds.Contains(u.Id.ToString()))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.FullName ?? u.Email ?? "Unknown");

        var canDecide = User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.TenantAdmin);

        ViewData["Title"] = $"Contract {contract.Reference}";

        return View(new ContractPageModel
        {
            Contract = contract,
            Document = DocumentFor(contract, names),
            LoanNumber = loanNumber,
            CanDecide = canDecide,
            CanCapture = canDecide || User.IsInRole(AppRoles.Capturer),
            MessagesStubbed = _contracts.MessagesAreStubbed,
            UserNames = names
        });
    }

    /// <summary>The pack as a PDF. <paramref name="inline"/> opens it in the browser; otherwise it downloads.</summary>
    [Authorize(Roles = ReadRoles)]
    public async Task<IActionResult> Pdf(Guid id, bool inline = false)
    {
        var contract = await LoadAsync(id);
        if (contract is null) return NotFound();

        var recordedBy = await NameOfAsync(contract.SignedRecordedByUserId);
        var bytes = _contracts.RenderPdf(contract, recordedBy);
        var name = FileNameFor(contract);

        if (inline)
        {
            Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return File(bytes, "application/pdf");
        }

        return File(bytes, "application/pdf", name);
    }

    // ---------------------------------------------------------------- send

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Send(Guid id, bool sms, bool email)
    {
        var contract = await LoadAsync(id);
        if (contract is null) return NotFound();

        try
        {
            var outcome = await _contracts.SendAsync(id, sms, email, PublicBaseUrl(), _users.GetUserId(User));

            var sent = string.Join(" and ", outcome.SentTo);
            var skipped = outcome.Skipped.Count == 0 ? "" : $" Not sent: {string.Join("; ", outcome.Skipped)}.";

            TempData["Success"] = outcome.Stubbed
                ? $"Recorded for sending by {sent} - no SMS or email provider is connected yet, so nothing " +
                  $"reached the client. The messages are under Messages sent.{skipped}"
                : $"Sent by {sent}.{skipped}";

            // Shown once, so it can be copied into WhatsApp or read out. Only its hash is stored.
            TempData["SigningLink"] = outcome.Link;
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- paper signature

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadSigned(Guid id, IFormFile? file, bool confirmed)
    {
        var contract = await LoadAsync(id);
        if (contract is null) return NotFound();

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose the scanned, signed contract to upload.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (!confirmed)
        {
            TempData["Error"] = "Confirm that the client signed this exact agreement before recording it.";
            return RedirectToAction(nameof(Details), new { id });
        }

        try
        {
            await using var content = file.OpenReadStream();
            await _contracts.RecordSignedCopyAsync(id, content, file.FileName, file.ContentType, _users.GetUserId(User));
            TempData["Success"] = $"Contract {contract.Reference} recorded as signed. The signed copy is filed with the client's documents.";
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- void

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanApproveCredit)]
    public async Task<IActionResult> Void(Guid id, string? reason)
    {
        var contract = await LoadAsync(id);
        if (contract is null) return NotFound();

        try
        {
            await _contracts.VoidAsync(id, reason, _users.GetUserId(User));
            TempData["Success"] = $"Contract {contract.Reference} voided. Its signing link no longer works.";
        }
        catch (ContractException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- helpers

    private async Task<LoanContract?> LoadAsync(Guid id)
    {
        var contract = await _db.LoanContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (contract is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(contract.TenantId);
        return contract;
    }

    private static ContractDocumentView DocumentFor(LoanContract c, IReadOnlyDictionary<string, string> names)
    {
        var recordedBy = c.SignedRecordedByUserId is { } u && names.TryGetValue(u, out var n) ? n : null;
        return new ContractDocumentView(
            ContractService.ReadSnapshot(c), c.SnapshotHash, ContractService.SignatureOf(c, recordedBy));
    }

    private async Task<string?> NameOfAsync(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        var user = await _users.FindByIdAsync(userId);
        return user?.FullName ?? user?.Email;
    }

    internal static string FileNameFor(LoanContract c) =>
        $"Agreement-{c.Reference.Replace('/', '-')}{(c.Status == Domain.Enums.ContractStatus.Signed ? "-signed" : "")}.pdf";

    /// <summary>Where the client's link points: configured for production, this request's host locally.</summary>
    private string PublicBaseUrl() =>
        string.IsNullOrWhiteSpace(_messaging.PublicBaseUrl)
            ? $"{Request.Scheme}://{Request.Host}"
            : _messaging.PublicBaseUrl;
}
