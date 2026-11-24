using System.Security.Cryptography;
using System.Text;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Domain.Portal;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Infrastructure.Netcash;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Netcash eMandates: the lender sends one from the loan, Netcash returns a page where the client
/// reads and signs it (shown in the client's app), and Netcash posts the result back here. A
/// signed mandate goes onto the debit order masterfile by itself (AddToMasterFile).
/// </summary>
public class EMandatesController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IConfiguration _config;
    private readonly ILogger<EMandatesController> _log;

    public EMandatesController(AppDbContext db, ICurrentTenant tenant, IConfiguration config, ILogger<EMandatesController> log)
    {
        _db = db;
        _tenant = tenant;
        _config = config;
        _log = log;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Send(Guid loanId, [FromServices] INetcashDebitOrderService netcash, [FromServices] ClientPortalPublisher publisher,
        [FromServices] INetcashSecretStore secrets)
    {
        var loan = await _db.Loans
            .Include(l => l.Client).ThenInclude(c => c!.Addresses)
            .Include(l => l.Client).ThenInclude(c => c!.BankAccounts)
            .Include(l => l.Schedule)
            .FirstOrDefaultAsync(l => l.Id == loanId);
        if (loan?.Client is null) return NotFound();
        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(loan.TenantId);

        var client = loan.Client;
        var back = RedirectToAction("Details", "Loans", new { id = loanId });
        var bank = client.BankAccounts.OrderByDescending(b => b.IsPrimary).FirstOrDefault();
        var mobile = ClientStatementBuilder.MobileOf(client);
        var first = loan.FirstCollectionDate ?? loan.Schedule.OrderBy(s => s.InstalmentNumber).FirstOrDefault()?.DueDate;

        var problem = bank is null ? "Capture the client's bank account first (Banking step)."
            : mobile is null ? "Capture the client's cell number first - Netcash sends the signing PIN to it."
            : first is null ? "The loan has no first collection date yet."
            : null;
        if (problem is not null) { TempData["Error"] = problem; return back; }

        var agreement = await _db.LoanContracts.AsNoTracking()
            .Where(c => c.LoanId == loan.Id && c.Status != ContractStatus.Voided)
            .OrderByDescending(c => c.Status == ContractStatus.Signed).ThenByDescending(c => c.CreatedUtc)
            .Select(c => c.Reference).FirstOrDefaultAsync();

        var reference = NetcashReference.Clean(loan.LoanNumber);
        var amount = loan.Schedule.Count > 0 ? loan.Schedule.Max(s => s.InstalmentTotal) : loan.FirstInstalment;
        var day = loan.CollectionDay is { Length: > 0 } d && (d == "LDOM" || int.TryParse(d, out _)) ? (d == "LDOM" ? d : d.PadLeft(2, '0')) : first.Value.Day.ToString("00");
        var email = client.Addresses.Select(a => a.EmailAddress).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));

        // The loan's own business key - not whichever business the user has selected.
        var slot = await _db.Tenants.AsNoTracking().Where(t => t.Id == loan.TenantId)
            .Select(t => new { t.NetcashAccountNumber, Key = t.ServiceKeys.Where(k => k.ServiceId == NetcashServiceId.DebitOrders).Select(k => new { k.SecretName, k.IsActive }).FirstOrDefault() })
            .FirstAsync();
        var serviceKey = slot.Key is { IsActive: false } ? null
            : await secrets.ResolveServiceKeyAsync(slot.Key?.SecretName ?? "", slot.NetcashAccountNumber, NetcashServiceId.DebitOrders);

        var result = await netcash.AddMandateAsync(serviceKey ?? string.Empty, new AddMandateRequest
        {
            AccountReference = reference,
            MandateName = $"{client.FirstName} {client.Surname}".Trim(),
            MandateAmount = amount,
            IsConsumer = true,
            FirstName = client.FirstName,
            Surname = client.Surname,
            IdNumber = client.IdNumber,
            MobileNumber = "0" + mobile![2..],
            EmailAddress = email,
            DebitFrequency = loan.Frequency,
            CommencementMonth = first.Value.Month,
            CommencementDay = day,
            AgreementDate = (loan.AgreementDate ?? DateTime.UtcNow).Date,
            AgreementReferenceNumber = agreement ?? loan.LoanNumber,
            BankAccountName = bank!.AccountHolderName,
            BankAccountNumber = bank.AccountNumber,
            BranchCode = bank.BranchCode,
            BankAccountType = bank.AccountType,
            Field1 = loan.LoanNumber,

            AddToMasterFile = true,
            MandateActive = true
        });

        if (!result.Success || result.Data?.MandateUrl is not { } url)
        {
            TempData["Error"] = result.Message ?? "Netcash did not create the eMandate.";
            return back;
        }

        var mandate = new DebiCheckMandate
        {
            TenantId = loan.TenantId, ClientId = client.Id, BankAccountId = bank.Id,
            MandateType = MandateType.NetcashEMandate, Status = MandateStatus.AwaitingDebtorAuthentication,
            AccountReference = reference, CollectionAmount = amount, Frequency = loan.Frequency,
            FirstCollectionDate = first, SigningUrl = url, SubmittedUtc = DateTime.UtcNow,
            CreatedByUserId = User.Identity?.Name
        };
        _db.Mandates.Add(mandate);
        _db.Add(new MandateEvent { MandateId = mandate.Id, EventType = "eMandateSent", ToStatus = mandate.Status, Detail = $"Netcash eMandate created; agreement {agreement ?? loan.LoanNumber}.", UserId = User.Identity?.Name });
        loan.MandateId = mandate.Id;
        await _db.SaveChangesAsync();

        var shown = await publisher.ShowInAppAsync(client.Id);
        TempData["Success"] = "eMandate created at Netcash. The client signs it in their app with a PIN Netcash sends to their cell. " + shown;
        return back;
    }

    /// <summary>For administrators: the address to paste into Netcash (NetConnector → Debit orders → eMandate postback).</summary>
    [HttpGet]
    [Authorize(Policy = AppPolicies.TenantAdministration)]
    public IActionResult Setup()
    {
        ViewData["Title"] = "eMandate setup";
        var secret = _config[LiveNetcashPayNowService.SecretSetting];
        ViewBag.Url = string.IsNullOrEmpty(secret) ? null : $"{Request.Scheme}://{Request.Host}/webhooks/netcash/emandate/{secret}";
        return View();
    }

    /// <summary>Netcash's eMandate postback: a form POST once the account holder has signed or declined.</summary>
    [HttpPost("/webhooks/netcash/emandate/{secret}")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Postback(string secret)
    {
        var expected = _config[LiveNetcashPayNowService.SecretSetting];
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return NotFound();

        if (!Request.HasFormContentType) return Ok();
        var f = await Request.ReadFormAsync();
        string? F(string k) => f.TryGetValue(k, out var v) ? v.ToString().Trim() : null;

        var reference = F("AccountRef");
        var mandate = await _db.Mandates
            .Where(m => m.MandateType == MandateType.NetcashEMandate && m.AccountReference == reference)
            .OrderByDescending(m => m.CreatedUtc).FirstOrDefaultAsync();
        if (mandate is null)
        {
            _log.LogWarning("eMandate postback for unknown reference {Reference}.", reference);
            return Ok();
        }

        var signed = F("MandateSuccessful") == "1" && F("IsDeclined") != "1";
        var from = mandate.Status;
        mandate.Status = signed ? MandateStatus.Authenticated : MandateStatus.Rejected;
        mandate.MandateReference = F("MandateReferenceNumber") ?? mandate.MandateReference;
        mandate.ResponseMessage = signed ? "Signed by the account holder" : F("ReasonForDecline") ?? "Declined";
        if (signed) mandate.AuthenticatedUtc = DateTime.UtcNow;
        if (F("MandatePDFLink") is { } pdf && pdf.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && pdf.Length <= 500)
            mandate.SignedPdfUrl = pdf;

        // The raw postback, without the bank account number.
        var raw = string.Join("&", f.Where(p => p.Key != "BankAccountNo").Select(p => $"{p.Key}={p.Value}"));
        _db.Add(new MandateEvent
        {
            MandateId = mandate.Id, EventType = "eMandatePostback", FromStatus = from, ToStatus = mandate.Status,
            Detail = mandate.ResponseMessage, RawPayload = raw.Length > 4000 ? raw[..4000] : raw
        });
        await _db.SaveChangesAsync();
        return Ok();
    }
}
