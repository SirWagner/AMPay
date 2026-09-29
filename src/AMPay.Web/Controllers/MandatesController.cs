using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Infrastructure.Netcash;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// DebiCheck mandate origination.
/// <para>
/// The two routes differ in more than a flag. TT1 answers inside the request, so the operator
/// finds out immediately. TT2 needs the masterfile entry to exist at Netcash first, then goes
/// up in a batch, and the bank answers later on a postback - so the mandate sits in
/// SubmittedToBank and the operator must not be told it succeeded.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.CanCapture)]
public class MandatesController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly INetcashDebiCheckService _debiCheck;
    private readonly INetcashDebitOrderService _debitOrders;
    private readonly INetcashSecretStore _secrets;
    private readonly INetcashCapabilityService _capabilities;
    private readonly ILogger<MandatesController> _log;

    public MandatesController(
        AppDbContext db,
        ICurrentTenant tenant,
        INetcashDebiCheckService debiCheck,
        INetcashDebitOrderService debitOrders,
        INetcashSecretStore secrets,
        INetcashCapabilityService capabilities,
        ILogger<MandatesController> log)
    {
        _db = db;
        _tenant = tenant;
        _debiCheck = debiCheck;
        _debitOrders = debitOrders;
        _secrets = secrets;
        _capabilities = capabilities;
        _log = log;
    }

    public async Task<IActionResult> Index(MandateStatus? status)
    {
        ViewData["Title"] = "DebiCheck mandates";
        await _tenant.LoadAsync();

        var query = _db.Mandates.AsNoTracking().Include(m => m.Client).AsQueryable();

        if (_tenant.TenantId is not null)
            query = query.Where(m => m.TenantId == _tenant.TenantId);
        else if (!_tenant.IsPlatformUser)
            return RedirectToAction("Index", "Tenants");

        if (status is not null) query = query.Where(m => m.Status == status);
        ViewBag.Status = status;

        var mandates = await query.OrderByDescending(m => m.CreatedUtc).Take(200).ToListAsync();
        return View(mandates);
    }

    [HttpGet]
    public async Task<IActionResult> Create(Guid clientId, Guid? loanId)
    {
        ViewData["Title"] = "New DebiCheck mandate";

        var client = await _db.Clients
            .Include(c => c.BankAccounts)
            .Include(c => c.Payback)
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == clientId);

        if (client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        if (client.BankAccounts.Count == 0)
        {
            TempData["Error"] = "Capture a bank account before creating a mandate.";
            return RedirectToAction("Banking", "Clients", new { id = clientId });
        }

        // Tell the operator before they fill in a form, not after they submit it.
        var capability = await _capabilities.GetAsync(client.TenantId, NetcashServiceId.DebitOrders);
        ViewBag.Capability = capability;

        var templates = capability.IsUsable
            ? await _debiCheck.RetrieveMandateTemplatesAsync(capability.ServiceKey!)
            : NetcashResult<IReadOnlyList<string>>.Ok(Array.Empty<string>());

        var model = new CreateMandateModel
        {
            ClientId = client.Id,
            ClientName = $"{client.FirstName} {client.Surname}".Trim(),
            ClientNumber = client.ClientNumber,
            AccountReference = client.ClientNumber,
            CollectionAmount = client.Payback?.InstalmentAmount ?? 0m,
            Frequency = client.Payback?.Frequency ?? DebitFrequency.Monthly,
            CollectionDayCode = client.Payback?.CollectionDayCode,
            FirstCollectionDate = client.Payback?.FirstCollectionDate,
            FirstCollectionDiffers = client.Payback?.FirstCollectionDiffers ?? false,
            FirstCollectionAmount = client.Payback?.FirstCollectionAmount,
            TrackingDays = client.Payback?.TrackingDays ?? 5,
            BankAccountId = client.BankAccounts.FirstOrDefault(a => a.IsPrimary)?.Id
                            ?? client.BankAccounts.First().Id,
            AvailableTemplates = templates.Data?.ToList() ?? new List<string>(),
            BankAccounts = client.BankAccounts.Select(a => new CreateMandateModel.BankAccountOption
            {
                Id = a.Id,
                Label = $"{a.BankName} {a.MaskedAccountNumber} ({a.AccountType})"
            }).ToList()
        };

        model.MandateTemplateId = model.AvailableTemplates.FirstOrDefault();

        // Originated from a loan: the loan is the source of truth for what is collected,
        // so its figures replace anything left over on the legacy payback tab.
        if (loanId is not null)
        {
            var (loan, problem) = await LoanForMandateAsync(loanId.Value, client.Id);
            if (loan is null)
            {
                TempData["Error"] = problem;
                return RedirectToAction("Details", "Loans", new { id = loanId });
            }

            model.LoanId = loan.Id;
            model.LoanNumber = loan.LoanNumber;

            // Each loan gets its own reference: field 101 must be unique in the Netcash
            // masterfile, and a client with two loans needs two mandates.
            model.AccountReference = loan.LoanNumber;

            // The first instalment is the largest, because credit life falls with the
            // balance - so it is the ceiling the mandate must allow.
            model.CollectionAmount = loan.FirstInstalment;
            model.Frequency = loan.Frequency;
            model.CollectionDayCode = loan.CollectionDayCode;
            model.FirstCollectionDate = loan.FirstCollectionDate;
            model.FirstCollectionDiffers = false;
            model.FirstCollectionAmount = null;
            model.TrackingDays = loan.TrackingDays;
        }

        return View(model);
    }

    /// <summary>
    /// The loan a mandate may be raised for: same client, approved or disbursed, and not
    /// already carrying one. Returns the reason when it cannot be used.
    /// </summary>
    private async Task<(Loan? Loan, string? Problem)> LoanForMandateAsync(Guid loanId, Guid clientId)
    {
        var loan = await _db.Loans.FirstOrDefaultAsync(l => l.Id == loanId);

        if (loan is null || loan.ClientId != clientId)
            return (null, "That loan does not belong to this client.");

        _tenant.EnsureCanAccess(loan.TenantId);

        if (loan.Status is not (LoanStatus.Approved or LoanStatus.Disbursed))
            return (null, "A DebiCheck mandate can only be originated for an approved loan.");

        // A mandate that failed, was rejected by the debtor, or expired unauthenticated does
        // not collect anything - so it must not stand in the way of trying again.
        if (loan.MandateId is not null)
        {
            var existing = await _db.Mandates.AsNoTracking()
                .Where(m => m.Id == loan.MandateId)
                .Select(m => (MandateStatus?)m.Status)
                .FirstOrDefaultAsync();

            if (existing is not null && !IsDeadMandate(existing.Value))
                return (null, "This loan already has a DebiCheck mandate.");
        }

        return (loan, null);
    }

    /// <summary>States from which a mandate will never collect.</summary>
    public static bool IsDeadMandate(MandateStatus status) =>
        status is MandateStatus.Failed or MandateStatus.Rejected
            or MandateStatus.Cancelled or MandateStatus.Expired;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateMandateModel model)
    {
        ViewData["Title"] = "New DebiCheck mandate";

        var client = await _db.Clients
            .Include(c => c.BankAccounts)
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == model.ClientId);

        if (client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        var account = client.BankAccounts.FirstOrDefault(a => a.Id == model.BankAccountId);
        if (account is null)
            ModelState.AddModelError(nameof(model.BankAccountId), "Choose a bank account.");

        var mobile = client.Addresses
            .Select(a => a.MobileNumber)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        if (string.IsNullOrWhiteSpace(mobile))
            ModelState.AddModelError(string.Empty,
                "This client has no mobile number. DebiCheck cannot authenticate without one.");

        if (model.MandateType == MandateType.DebiCheckTt1RealTime
            && string.IsNullOrWhiteSpace(model.MandateTemplateId))
            ModelState.AddModelError(nameof(model.MandateTemplateId),
                "Real-time TT1 requires a mandate template configured for real-time use.");

        if (model.FirstCollectionDiffers && model.FirstCollectionAmount is null)
            ModelState.AddModelError(nameof(model.FirstCollectionAmount),
                "Enter the first collection amount.");

        // Re-checked on post: the loan could have been cancelled, or given a mandate by a
        // colleague, while this form sat open.
        Loan? loan = null;
        if (model.LoanId is not null)
        {
            (loan, var problem) = await LoanForMandateAsync(model.LoanId.Value, client.Id);
            if (loan is null) ModelState.AddModelError(string.Empty, problem!);
        }

        if (!ModelState.IsValid)
        {
            await RepopulateAsync(model, client);
            return View(model);
        }

        var capability = await _capabilities.GetAsync(client.TenantId, NetcashServiceId.DebitOrders);
        if (!capability.IsUsable)
        {
            // Nothing is written. The mandate is not created in a half-state that an operator
            // would later have to reconcile against a Netcash account that never heard of it.
            _log.LogWarning("Mandate blocked for tenant {TenantId}: {State} - {Message}",
                client.TenantId, capability.State, capability.Message);

            ViewBag.Capability = capability;
            ModelState.AddModelError(string.Empty,
                capability.Message + (capability.Remedy is null ? "" : " " + capability.Remedy));

            await RepopulateAsync(model, client);
            return View(model);
        }

        var serviceKey = capability.ServiceKey!;

        var mandate = new DebiCheckMandate
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            BankAccountId = account!.Id,
            MandateType = model.MandateType,
            AccountReference = model.AccountReference.Trim(),
            MandateTemplateId = model.MandateTemplateId,
            CollectionAmount = model.CollectionAmount,
            FirstCollectionDiffers = model.FirstCollectionDiffers,
            FirstCollectionAmount = model.FirstCollectionDiffers ? model.FirstCollectionAmount : null,
            FirstCollectionDate = model.FirstCollectionDate,
            Frequency = model.Frequency,
            CollectionDayCode = model.CollectionDayCode,
            TrackingDays = model.TrackingDays,
            Status = MandateStatus.Draft,
            CreatedByUserId = User.Identity?.Name
        };

        _db.Mandates.Add(mandate);

        // Linked before the Netcash call rather than after it, so a mandate that fails at
        // the bank is still visibly attached to the loan it was meant to collect.
        if (loan is not null) loan.MandateId = mandate.Id;

        await _db.SaveChangesAsync();

        var request = new DebiCheckAuthenticateRequest
        {
            AccountReference = mandate.AccountReference,
            MandateTemplateId = mandate.MandateTemplateId ?? string.Empty,
            IsIdNumber = client.IsSaIdNumber,
            DebtorIdentification = client.IdNumber,
            AccountName = $"{client.FirstName} {client.Surname}".Trim(),
            BankAccountName = account.AccountHolderName,
            BranchCode = account.BranchCode,
            BankAccountNumber = account.AccountNumber,
            BankAccountType = account.AccountType,
            MobileNumber = mobile!,
            EmailAddress = client.Addresses.Select(a => a.EmailAddress)
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
            CollectionAmount = mandate.CollectionAmount,
            FirstCollectionDiffers = mandate.FirstCollectionDiffers,
            FirstCollectionAmount = mandate.FirstCollectionAmount,
            FirstCollectionDate = mandate.FirstCollectionDate,
            CollectionDayCode = mandate.CollectionDayCode
        };

        if (mandate.MandateType == MandateType.DebiCheckTt1RealTime)
            await SubmitRealTimeAsync(mandate, request, serviceKey);
        else
            await SubmitBatchAsync(mandate, request, serviceKey, client);

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Details), new { id = mandate.Id });
    }

    /// <summary>TT1: the bank answers inside this call.</summary>
    private async Task SubmitRealTimeAsync(
        DebiCheckMandate mandate, DebiCheckAuthenticateRequest request, string serviceKey)
    {
        mandate.SubmittedUtc = DateTime.UtcNow;
        AddEvent(mandate, "Submitted", MandateStatus.Draft, MandateStatus.SubmittedToBank,
            "TT1 real-time authentication submitted.");

        var result = await _debiCheck.AuthenticateRealTimeAsync(serviceKey, request);

        mandate.ErrorCode = result.Code;
        mandate.ResponseMessage = result.Message;

        if (!result.Success || result.Data is null)
        {
            mandate.Status = MandateStatus.Failed;
            AddEvent(mandate, "Failed", MandateStatus.SubmittedToBank, MandateStatus.Failed,
                result.Message ?? "TT1 submission failed.");

            TempData["Error"] = result.Message ?? "The mandate could not be submitted.";
            return;
        }

        var data = result.Data;
        mandate.ContractReference = data.ContractReference;
        mandate.BankResponseCode = data.BankResponseCode;
        mandate.BankservResponseCode = data.BankservResponseCode;
        mandate.ClientResponseCode = data.ClientResponseCode;

        if (data.IsAccepted)
        {
            mandate.Status = MandateStatus.Authenticated;
            mandate.AuthenticatedUtc = DateTime.UtcNow;
            AddEvent(mandate, "Authenticated", MandateStatus.SubmittedToBank, MandateStatus.Authenticated,
                $"Accepted by the bank. Contract reference {data.ContractReference}.");

            TempData["Success"] = "The debtor bank authenticated this mandate. Collections can begin.";
        }
        else
        {
            mandate.Status = MandateStatus.Rejected;
            AddEvent(mandate, "Rejected", MandateStatus.SubmittedToBank, MandateStatus.Rejected,
                $"Bank response {data.BankResponseCode}, Bankserv {data.BankservResponseCode}.");

            TempData["Error"] = "The debtor bank rejected this mandate.";
        }
    }

    /// <summary>
    /// TT2: masterfile first, then the authentication batch. The outcome arrives later on
    /// the NetConnector postback, so the mandate is left waiting rather than marked good.
    /// </summary>
    private async Task SubmitBatchAsync(
        DebiCheckMandate mandate,
        DebiCheckAuthenticateRequest request,
        string serviceKey,
        Client client)
    {
        mandate.Status = MandateStatus.PendingMasterfile;

        var vendorKey = await _secrets.GetSoftwareVendorKeyAsync();
        var actionDate = mandate.FirstCollectionDate ?? DateTime.UtcNow.Date.AddDays(2);

        // Step one: the masterfile entry. Netcash rejects a DebiCheckAuthentication
        // instruction for an account it does not already hold.
        var masterfile = new NifFileBuilder(
                serviceKey, vendorKey, NifInstruction.Update,
                $"MF-{mandate.AccountReference}", actionDate)
            .WithAmountField(NifField.DefaultDebitAmount)
            .AddTransaction(t => t
                .Set(NifField.AccountReference, request.AccountReference)
                .Set(NifField.AccountName, request.AccountName)
                .Set(NifField.BankingDetailType, (int)BankingDetailType.BankAccount)
                .Set(NifField.BankAccountName, request.BankAccountName)
                .Set(NifField.BankAccountType, (int)request.BankAccountType)
                .Set(NifField.BranchCode, request.BranchCode)
                .Set(NifField.Filler, string.Empty)
                .Set(NifField.AccountNumber, request.BankAccountNumber)
                .SetAmount(NifField.DefaultDebitAmount, request.CollectionAmount)
                .SetAmount(NifField.Amount, request.CollectionAmount))
            .Build();

        var masterfileResult = await _debitOrders.UploadMasterfileAsync(serviceKey, masterfile);

        if (!masterfileResult.Success)
        {
            mandate.Status = MandateStatus.Failed;
            mandate.ErrorCode = masterfileResult.Code;
            mandate.ResponseMessage = masterfileResult.Message;

            AddEvent(mandate, "MasterfileFailed", MandateStatus.PendingMasterfile, MandateStatus.Failed,
                masterfileResult.Message ?? "Masterfile upload failed.");

            TempData["Error"] =
                $"The masterfile upload failed, so the mandate was not submitted. {masterfileResult.Message}";
            return;
        }

        AddEvent(mandate, "MasterfileUploaded", MandateStatus.PendingMasterfile, MandateStatus.PendingMasterfile,
            $"Masterfile batch accepted, file token {masterfileResult.Data}.");

        // Step two: the authentication request itself.
        var batchResult = await _debiCheck.AuthenticateBatchAsync(
            serviceKey, new[] { request }, actionDate, $"DC-{mandate.AccountReference}");

        mandate.ErrorCode = batchResult.Code;
        mandate.ResponseMessage = batchResult.Message;

        if (!batchResult.Success)
        {
            mandate.Status = MandateStatus.Failed;
            AddEvent(mandate, "Failed", MandateStatus.PendingMasterfile, MandateStatus.Failed,
                batchResult.Message ?? "Batch authentication failed.");

            TempData["Error"] = batchResult.Message ?? "The mandate batch was rejected.";
            return;
        }

        mandate.FileToken = batchResult.Data;
        mandate.SubmittedUtc = DateTime.UtcNow;
        mandate.Status = MandateStatus.SubmittedToBank;

        AddEvent(mandate, "Submitted", MandateStatus.PendingMasterfile, MandateStatus.SubmittedToBank,
            $"Authentication batch accepted, file token {batchResult.Data}. Awaiting the bank postback.");

        TempData["Info"] =
            "The mandate has been submitted for batch authentication. " +
            "A file token is not an approval - the bank result arrives on the NetConnector postback.";
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        ViewData["Title"] = "Mandate";

        var mandate = await _db.Mandates
            .Include(m => m.Client)
            .Include(m => m.BankAccount)
            .Include(m => m.Events)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (mandate is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(mandate.TenantId);

        return View(mandate);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, string reasonCode)
    {
        var mandate = await _db.Mandates.FirstOrDefaultAsync(m => m.Id == id);
        if (mandate is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(mandate.TenantId);

        if (string.IsNullOrWhiteSpace(mandate.ContractReference))
        {
            // Nothing reached the bank, so there is nothing at Netcash to cancel.
            var from = mandate.Status;
            mandate.Status = MandateStatus.Cancelled;
            mandate.CancelledUtc = DateTime.UtcNow;
            AddEvent(mandate, "Cancelled", from, MandateStatus.Cancelled, "Cancelled locally before submission.");
            await _db.SaveChangesAsync();

            TempData["Success"] = "Mandate cancelled.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var capability = await _capabilities.GetAsync(mandate.TenantId, NetcashServiceId.DebitOrders);
        if (!capability.IsUsable)
        {
            // The mandate stays as it is. Marking it cancelled locally while Netcash still
            // holds a live authority would leave the two systems disagreeing about whether
            // this debtor can be collected from.
            TempData["Error"] = capability.Message +
                (capability.Remedy is null ? "" : " " + capability.Remedy);

            return RedirectToAction(nameof(Details), new { id });
        }

        var serviceKey = capability.ServiceKey!;

        var result = await _debiCheck.CancelAuthenticationAsync(
            serviceKey, mandate.ContractReference, reasonCode ?? "01");

        if (result.Success)
        {
            var from = mandate.Status;
            mandate.Status = MandateStatus.Cancelled;
            mandate.CancelledUtc = DateTime.UtcNow;
            AddEvent(mandate, "Cancelled", from, MandateStatus.Cancelled,
                $"Cancellation sent to Netcash, reason {reasonCode}.");

            TempData["Success"] = "Cancellation submitted.";
        }
        else
        {
            TempData["Error"] = result.Message ?? "The cancellation was rejected.";
        }

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------- helpers

    private void AddEvent(
        DebiCheckMandate mandate, string type, MandateStatus? from, MandateStatus? to, string detail)
    {
        // Registered on the DbSet rather than on mandate.Events: these entities set their own
        // Guid key, so attaching through a tracked parent's navigation makes EF treat the row
        // as pre-existing and emit an UPDATE instead of an INSERT.
        _db.MandateEvents.Add(new MandateEvent
        {
            MandateId = mandate.Id,
            EventType = type,
            FromStatus = from,
            ToStatus = to,
            Detail = detail,
            UserId = User.Identity?.Name
        });

        _log.LogInformation("Mandate {Reference}: {Type} - {Detail}",
            mandate.AccountReference, type, detail);
    }

    /// <summary>
    /// Resolves the debit order key through the capability service, which is the only thing
    /// allowed to decide whether a key is usable. It returns null rather than a placeholder
    /// when stubs are off and the key is missing, so a configuration gap can never be sent to
    /// Netcash as a bad credential.
    /// </summary>
    private async Task<string?> ResolveDebitOrderKeyAsync(Guid tenantId)
    {
        var capability = await _capabilities.GetAsync(tenantId, NetcashServiceId.DebitOrders);
        return capability.IsUsable ? capability.ServiceKey : null;
    }

    private async Task RepopulateAsync(CreateMandateModel model, Client client)
    {
        model.ClientName = $"{client.FirstName} {client.Surname}".Trim();
        model.ClientNumber = client.ClientNumber;
        model.BankAccounts = client.BankAccounts.Select(a => new CreateMandateModel.BankAccountOption
        {
            Id = a.Id,
            Label = $"{a.BankName} {a.MaskedAccountNumber} ({a.AccountType})"
        }).ToList();

        var key = await ResolveDebitOrderKeyAsync(client.TenantId);

        var templates = key is null
            ? NetcashResult<IReadOnlyList<string>>.Ok(Array.Empty<string>())
            : await _debiCheck.RetrieveMandateTemplatesAsync(key);

        model.AvailableTemplates = templates.Data?.ToList() ?? new List<string>();
    }
}
