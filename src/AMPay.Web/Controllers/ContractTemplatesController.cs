using AMPay.Domain.Enums;
using AMPay.Infrastructure.Contracts;
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
/// The lender's own contract wording, one section per kind.
/// <para>
/// The figures in a pack come from the loan; the words come from here. AM-Pay ships
/// placeholder wording only. Approving a section is the lender's statement that its attorney
/// has signed it off - until every section in a pack is approved, the pack carries a DRAFT
/// mark and, once real messaging is connected, cannot be sent or signed. Changing approved
/// wording withdraws the approval.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.TenantAdministration)]
public class ContractTemplatesController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly ContractService _contracts;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<ContractTemplatesController> _log;

    public ContractTemplatesController(
        AppDbContext db,
        ICurrentTenant tenant,
        ContractService contracts,
        UserManager<ApplicationUser> users,
        ILogger<ContractTemplatesController> log)
    {
        _db = db;
        _tenant = tenant;
        _contracts = contracts;
        _users = users;
        _log = log;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Contract wording";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null)
        {
            TempData["Info"] = "Choose a customer account to see its contract wording.";
            return RedirectToAction("Index", "Tenants");
        }

        await _contracts.EnsureTemplatesAsync(_tenant.TenantId.Value);

        var templates = await _db.ContractTemplates.AsNoTracking()
            .Where(t => t.TenantId == _tenant.TenantId)
            .OrderBy(t => t.Kind)
            .ToListAsync();

        ViewBag.CustomerName = _tenant.TenantName;
        return View(templates);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var t = await _db.ContractTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(t.TenantId);

        ViewData["Title"] = t.Title;

        var approvedBy = t.ApprovedByUserId is null ? null : await _users.FindByIdAsync(t.ApprovedByUserId);

        return View(new ContractTemplateEditModel
        {
            Id = t.Id,
            Kind = t.Kind,
            Title = t.Title,
            Body = t.Body,
            IsApproved = t.IsApproved,
            ApprovedUtc = t.ApprovedUtc,
            ApprovedBy = approvedBy?.FullName ?? approvedBy?.Email
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ContractTemplateEditModel model)
    {
        var t = await _db.ContractTemplates.FirstOrDefaultAsync(x => x.Id == model.Id);
        if (t is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(t.TenantId);

        ViewData["Title"] = t.Title;
        if (!ModelState.IsValid)
        {
            model.Kind = t.Kind;
            model.IsApproved = t.IsApproved;
            return View(model);
        }

        var body = model.Body.Replace("\r\n", "\n").Trim();
        var title = model.Title.Trim();
        var changed = body != t.Body || title != t.Title;

        if (changed)
        {
            t.Body = body;
            t.Title = title;
            t.UpdatedUtc = DateTime.UtcNow;
            t.UpdatedByUserId = _users.GetUserId(User);

            // Approval covers the words that were approved, not whatever is there now.
            var wasApproved = t.IsApproved;
            t.IsApproved = false;
            t.ApprovedUtc = null;
            t.ApprovedByUserId = null;

            await _db.SaveChangesAsync();

            _log.LogInformation("Contract template {Kind} changed for tenant {TenantId}.", t.Kind, t.TenantId);

            TempData["Success"] = wasApproved
                ? $"{t.Title} saved. Its approval has been withdrawn - approve it again once your attorney has signed off the new wording."
                : $"{t.Title} saved.";
        }
        else
        {
            TempData["Info"] = "Nothing changed.";
        }

        return RedirectToAction(nameof(Edit), new { id = t.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, bool attorneyConfirmed)
    {
        var t = await _db.ContractTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(t.TenantId);

        if (!attorneyConfirmed)
        {
            TempData["Error"] = "Confirm that your attorney has approved this wording.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (t.Body.StartsWith("DRAFT WORDING", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "This is still AM-Pay's placeholder wording. Replace it with your attorney's wording " +
                                "(and remove the DRAFT WORDING line) before approving it.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        t.IsApproved = true;
        t.ApprovedUtc = DateTime.UtcNow;
        t.ApprovedByUserId = _users.GetUserId(User);
        await _db.SaveChangesAsync();

        _log.LogInformation("Contract template {Kind} approved for tenant {TenantId}.", t.Kind, t.TenantId);

        TempData["Success"] = $"{t.Title} approved. Contracts issued from now on use this wording.";
        return RedirectToAction(nameof(Index));
    }

    internal static string Describe(ContractTemplateKind k) => k switch
    {
        ContractTemplateKind.CreditAgreementTerms => "The general terms of the credit agreement.",
        ContractTemplateKind.DebitOrderAuthorisation => "Printed under the DebiCheck mandate details.",
        ContractTemplateKind.BudgetAcknowledgement => "Printed under the client's budget and NET of NET.",
        ContractTemplateKind.CreditLifeDisclosure => "Printed when the loan carries credit life insurance.",
        _ => ""
    };
}
