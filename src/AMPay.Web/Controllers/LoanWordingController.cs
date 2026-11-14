using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Contract wording for one borrower. The lender's standard wording (Contract wording, under
/// Settings) serves every client; here a section is reworded for a single loan before its
/// contract is issued - a special condition agreed with this client, or their own attorney's
/// document. The standard wording is never changed from here.
/// <para>
/// Administrators only: this is legal text. A one-off wording carries its own approval, and a
/// contract issued from unapproved wording is a draft, exactly as with the standard sections.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class LoanWordingController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly ContractService _contracts;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<LoanWordingController> _log;

    public LoanWordingController(
        AppDbContext db,
        ICurrentTenant tenant,
        ContractService contracts,
        UserManager<ApplicationUser> users,
        ILogger<LoanWordingController> log)
    {
        _db = db;
        _tenant = tenant;
        _contracts = contracts;
        _users = users;
        _log = log;
    }

    public async Task<IActionResult> Index(Guid loanId)
    {
        var loan = await LoadLoanAsync(loanId);
        if (loan is null) return NotFound();

        var templates = await TemplatesAsync(loan.TenantId);
        var own = await _db.LoanWordings.AsNoTracking().Where(w => w.LoanId == loanId).ToDictionaryAsync(w => w.Kind);
        var contracts = await ContractsAsync(loanId);

        ViewData["Title"] = "Wording for this client";
        return View(new LoanWordingIndexModel
        {
            Loan = loan,
            LockedReason = LockedReason(loan, contracts),
            OpenContract = contracts.FirstOrDefault(c => c.IsOpen)?.Reference,
            Sections = Kinds(loan).Select(k => new LoanWordingIndexModel.Section
            {
                Kind = k,
                Title = own.TryGetValue(k, out var w) ? w.Title : templates[k].Title,
                Own = w,
                StandardApproved = templates[k].IsApproved
            }).ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid loanId, ContractTemplateKind kind)
    {
        var loan = await LoadLoanAsync(loanId);
        if (loan is null || !Kinds(loan).Contains(kind)) return NotFound();

        var standard = (await TemplatesAsync(loan.TenantId))[kind];
        var own = await _db.LoanWordings.AsNoTracking().FirstOrDefaultAsync(w => w.LoanId == loanId && w.Kind == kind);

        return await EditViewAsync(loan, standard, new LoanWordingEditModel
        {
            LoanId = loanId,
            Kind = kind,
            Title = own?.Title ?? standard.Title,
            Body = own?.Body ?? standard.Body,
            Reason = own?.Reason,
            Approved = own?.IsApproved ?? false
        }, own is not null);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(LoanWordingEditModel model)
    {
        var loan = await LoadLoanAsync(model.LoanId);
        if (loan is null || !Kinds(loan).Contains(model.Kind)) return NotFound();

        if (LockedReason(loan, await ContractsAsync(loan.Id)) is { } locked)
        {
            TempData["Error"] = locked;
            return RedirectToAction(nameof(Index), new { loanId = loan.Id });
        }

        var standard = (await TemplatesAsync(loan.TenantId))[model.Kind];
        var own = await _db.LoanWordings.FirstOrDefaultAsync(w => w.LoanId == loan.Id && w.Kind == model.Kind);

        var body = (model.Body ?? "").Replace("\r\n", "\n").Trim();
        var title = (model.Title ?? "").Trim();

        var unknown = ContractMergeFields.Unknown(body);
        if (unknown.Count > 0)
            ModelState.AddModelError(nameof(model.Body),
                "These fields do not exist: " + string.Join(", ", unknown.Select(u => "{{" + u + "}}")) +
                ". Use the fields listed under the editor, or remove the double braces.");

        // Back to exactly the standard text is not a change for this client at all.
        var isStandard = body == standard.Body && title == standard.Title;
        if (!isStandard && string.IsNullOrWhiteSpace(model.Reason))
            ModelState.AddModelError(nameof(model.Reason), "Say why this client's wording is different.");

        if (!ModelState.IsValid)
        {
            model.Body = body;
            return await EditViewAsync(loan, standard, model, own is not null);
        }

        if (isStandard)
        {
            if (own is not null) _db.LoanWordings.Remove(own);
            await _db.SaveChangesAsync();
            TempData["Info"] = "That is your standard wording, so nothing is different for this client.";
            return RedirectToAction(nameof(Index), new { loanId = loan.Id });
        }

        var userId = _users.GetUserId(User);
        var now = DateTime.UtcNow;
        if (own is null)
        {
            own = new LoanWording { LoanId = loan.Id, Kind = model.Kind };
            _db.LoanWordings.Add(own);
        }

        // Approval covers the words that were approved: any change to them needs the tick again.
        var changed = own.Body != body || own.Title != title;
        own.Title = title;
        own.Body = body;
        own.Reason = model.Reason!.Trim();
        own.UpdatedUtc = now;
        own.UpdatedByUserId = userId;
        if (model.Approved && (changed || !own.IsApproved))
        {
            own.IsApproved = true;
            own.ApprovedUtc = now;
            own.ApprovedByUserId = userId;
        }
        else if (!model.Approved)
        {
            own.IsApproved = false;
            own.ApprovedUtc = null;
            own.ApprovedByUserId = null;
        }

        await _db.SaveChangesAsync();
        _log.LogInformation("Wording {Kind} changed for loan {Loan}.", model.Kind, loan.LoanNumber);

        var open = (await ContractsAsync(loan.Id)).FirstOrDefault(c => c.IsOpen);
        TempData["Success"] = $"{title} saved for {loan.Client!.FullName}." +
            (own.IsApproved ? "" : " It is not marked approved, so a contract issued from it will be a draft.") +
            (open is null ? "" : $" Contract {open.Reference} is already issued and keeps its wording: issue it again to use this.");
        return RedirectToAction(nameof(Index), new { loanId = loan.Id });
    }

    /// <summary>
    /// Pulls the text out of an uploaded Word or text file into the editor, unsaved, for this
    /// client's section. Same reader as the standard wording.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(DocumentText.MaxBytes + 64 * 1024)]
    public async Task<IActionResult> Extract(Guid loanId, ContractTemplateKind kind, IFormFile? file, bool append, string? reason)
    {
        var loan = await LoadLoanAsync(loanId);
        if (loan is null || !Kinds(loan).Contains(kind)) return NotFound();

        if (file is null || file.Length == 0 || file.Length > DocumentText.MaxBytes)
        {
            TempData["Error"] = "Choose a Word document (.docx) or text file (.txt) of 5 MB or less.";
            return RedirectToAction(nameof(Edit), new { loanId, kind });
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        var (text, error) = DocumentText.Extract(file.FileName, buffer);
        if (text is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Edit), new { loanId, kind });
        }

        var standard = (await TemplatesAsync(loan.TenantId))[kind];
        var own = await _db.LoanWordings.AsNoTracking().FirstOrDefaultAsync(w => w.LoanId == loanId && w.Kind == kind);
        var current = own?.Body ?? standard.Body;

        ViewBag.Extracted = Path.GetFileName(file.FileName);
        return await EditViewAsync(loan, standard, new LoanWordingEditModel
        {
            LoanId = loanId,
            Kind = kind,
            Title = own?.Title ?? standard.Title,
            Body = append ? current + "\n\n" + text : text,
            Reason = own?.Reason ?? reason
        }, own is not null);
    }

    /// <summary>Drops this client's version of a section: their contract uses the standard wording again.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(Guid loanId, ContractTemplateKind kind)
    {
        var loan = await LoadLoanAsync(loanId);
        if (loan is null) return NotFound();

        if (LockedReason(loan, await ContractsAsync(loan.Id)) is { } locked)
        {
            TempData["Error"] = locked;
            return RedirectToAction(nameof(Index), new { loanId });
        }

        var own = await _db.LoanWordings.FirstOrDefaultAsync(w => w.LoanId == loanId && w.Kind == kind);
        if (own is not null)
        {
            _db.LoanWordings.Remove(own);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Back to your standard wording for this section.";
        }
        return RedirectToAction(nameof(Index), new { loanId });
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IActionResult> EditViewAsync(Loan loan, ContractTemplate standard, LoanWordingEditModel model, bool isOwn)
    {
        ViewData["Title"] = model.Title;
        model.LoanNumber = loan.LoanNumber;
        model.ClientName = loan.Client!.FullName;
        model.IsOwn = isOwn;
        model.StandardBody = standard.Body;
        model.LockedReason = LockedReason(loan, await ContractsAsync(loan.Id));
        return View(nameof(Edit), model);
    }

    private async Task<Loan?> LoadLoanAsync(Guid loanId)
    {
        var loan = await _db.Loans.AsNoTracking().Include(l => l.Client).FirstOrDefaultAsync(l => l.Id == loanId);
        if (loan?.Client is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(loan.TenantId);
        return loan;
    }

    private async Task<Dictionary<ContractTemplateKind, ContractTemplate>> TemplatesAsync(Guid tenantId)
    {
        await _contracts.EnsureTemplatesAsync(tenantId);
        return await _db.ContractTemplates.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.Kind);
    }

    private Task<List<LoanContract>> ContractsAsync(Guid loanId) =>
        _db.LoanContracts.AsNoTracking().Where(c => c.LoanId == loanId).ToListAsync();

    /// <summary>The sections this loan's contract prints, in the order it prints them.</summary>
    internal static IReadOnlyList<ContractTemplateKind> Kinds(Loan loan) => new[]
        {
            ContractTemplateKind.BorrowerDeclarations,
            ContractTemplateKind.CreditAgreementTerms,
            ContractTemplateKind.BudgetAcknowledgement,
            ContractTemplateKind.CreditLifeDisclosure,
            ContractTemplateKind.DebitOrderAuthorisation
        }
        .Where(k => k != ContractTemplateKind.CreditLifeDisclosure || loan.TotalCreditLife > 0)
        .ToList();

    /// <summary>Why the wording can no longer change, or null while it still can.</summary>
    internal static string? LockedReason(Loan loan, IEnumerable<LoanContract> contracts)
    {
        if (contracts.Any(c => c.Status == ContractStatus.Signed))
            return "The client has signed the contract. Its wording is final.";
        if (loan.Status is not (LoanStatus.Draft or LoanStatus.PendingApproval or LoanStatus.Approved))
            return "Wording can be changed only before the loan is paid out.";
        return null;
    }
}

public class LoanWordingIndexModel
{
    public Loan Loan { get; set; } = null!;
    public string? LockedReason { get; set; }
    public string? OpenContract { get; set; }
    public List<Section> Sections { get; set; } = new();

    public class Section
    {
        public ContractTemplateKind Kind { get; set; }
        public string Title { get; set; } = "";
        public LoanWording? Own { get; set; }
        public bool StandardApproved { get; set; }
    }
}

public class LoanWordingEditModel
{
    public Guid LoanId { get; set; }
    public ContractTemplateKind Kind { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Heading")]
    public string Title { get; set; } = "";

    [Required, StringLength(40000, MinimumLength = 20)]
    [Display(Name = "Wording for this client")]
    public string Body { get; set; } = "";

    [StringLength(500)]
    [Display(Name = "Why is this client's wording different?")]
    public string? Reason { get; set; }

    public bool Approved { get; set; }

    // Display only.
    public string LoanNumber { get; set; } = "";
    public string ClientName { get; set; } = "";
    public bool IsOwn { get; set; }
    public string StandardBody { get; set; } = "";
    public string? LockedReason { get; set; }
}
