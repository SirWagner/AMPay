using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Pay Now checkout.
/// <para>
/// The customer browser is posted across to the Netcash hosted payment page. No card data
/// ever touches this application, which is what keeps AM-Pay inside PCI DSS SAQ A rather
/// than the far heavier SAQ D - so the redirect must stay a redirect. Never proxy the card
/// form, however convenient it looks.
/// </para>
/// </summary>
public class CheckoutController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly INetcashPayNowService _payNow;
    private readonly INetcashSecretStore _secrets;
    private readonly INetcashCapabilityService _capabilities;
    private readonly ILogger<CheckoutController> _log;

    public CheckoutController(
        AppDbContext db,
        ICurrentTenant tenant,
        INetcashPayNowService payNow,
        INetcashSecretStore secrets,
        INetcashCapabilityService capabilities,
        ILogger<CheckoutController> log)
    {
        _db = db;
        _tenant = tenant;
        _payNow = payNow;
        _secrets = secrets;
        _capabilities = capabilities;
        _log = log;
    }

    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Pay Now";
        await _tenant.LoadAsync();

        var query = _db.PayNowTransactions.AsNoTracking().AsQueryable();
        if (_tenant.TenantId is not null)
            query = query.Where(p => p.TenantId == _tenant.TenantId);

        ViewBag.Transactions = await query
            .OrderByDescending(p => p.CreatedUtc)
            .Take(100)
            .ToListAsync();

        if (_tenant.TenantId is not null)
            ViewBag.Capability = await _capabilities.GetAsync(
                _tenant.TenantId.Value, NetcashServiceId.PayNow);

        return View(new CheckoutModel());
    }

    /// <summary>Creates the transaction and shows the customer-facing payment page.</summary>
    [HttpPost]
    [Authorize(Policy = AppPolicies.CanCapture)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CheckoutModel model)
    {
        ViewData["Title"] = "Pay Now";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null)
        {
            TempData["Error"] = "Select a customer account first.";
            return RedirectToAction("Index", "Tenants");
        }

        if (!ModelState.IsValid)
        {
            ViewBag.Transactions = await _db.PayNowTransactions.AsNoTracking()
                .Where(p => p.TenantId == _tenant.TenantId)
                .OrderByDescending(p => p.CreatedUtc).Take(100).ToListAsync();
            return View("Index", model);
        }

        var transaction = new PayNowTransaction
        {
            TenantId = _tenant.TenantId.Value,
            ClientId = model.ClientId,
            PaymentReference = $"AMP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}",
            Amount = model.Amount,
            Description = model.Description,
            CustomerName = model.CustomerName,
            CustomerEmail = model.CustomerEmail,
            CustomerMobile = model.CustomerMobile,
            Status = PaymentStatus.Created
        };

        _db.PayNowTransactions.Add(transaction);
        await _db.SaveChangesAsync();

        _log.LogInformation("Pay Now transaction {Reference} created for {Amount}.",
            transaction.PaymentReference, transaction.Amount);

        return RedirectToAction(nameof(Pay), new { reference = transaction.PaymentReference });
    }

    /// <summary>
    /// The customer-facing payment page. Anonymous by design - this link is what gets sent
    /// to the payer, who has no account here.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Pay(string reference)
    {
        var transaction = await _db.PayNowTransactions
            .Include(p => p.Tenant)
            .FirstOrDefaultAsync(p => p.PaymentReference == reference);

        if (transaction is null) return NotFound();

        if (transaction.Status is PaymentStatus.Complete)
            return RedirectToAction("Accepted", new { reference });

        var serviceKey = await ResolvePayNowKeyAsync(transaction.TenantId);
        if (serviceKey is null)
        {
            _log.LogError("No Pay Now service key for tenant {TenantId}.", transaction.TenantId);
            return View("Unavailable");
        }

        var form = await _payNow.BuildCheckoutAsync(serviceKey, new PayNowCheckoutRequest
        {
            PaymentReference = transaction.PaymentReference,
            Amount = transaction.Amount,
            Description = transaction.Description,
            CustomerName = transaction.CustomerName,
            CustomerEmail = transaction.CustomerEmail,
            CustomerMobile = transaction.CustomerMobile,
            ExtraField1 = transaction.Id.ToString()
        });

        if (!form.Success || form.Data is null)
        {
            _log.LogError("Pay Now checkout build failed: {Message}", form.Message);
            return View("Unavailable");
        }

        transaction.Status = PaymentStatus.Redirected;
        transaction.RedirectedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        ViewData["Title"] = "Payment";
        ViewBag.Form = form.Data;
        return View(transaction);
    }

    // ------------------------------------------------------- Netcash callbacks

    /// <summary>
    /// Browser redirect after a successful payment. Confirms to the customer only.
    /// <para>
    /// This is not proof of payment. It is a redirect the customer browser performs and can
    /// be forged or replayed - <see cref="Notify"/> is the authoritative signal.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet, HttpPost]
    [IgnoreAntiforgeryToken]
    [ActionName("Accepted")] // the method is renamed only to avoid hiding ControllerBase.Accepted
    public async Task<IActionResult> PaymentAccepted(string? reference)
    {
        ViewData["Title"] = "Payment received";

        reference ??= Request.HasFormContentType ? Request.Form["p2"].ToString() : null;

        var transaction = string.IsNullOrWhiteSpace(reference)
            ? null
            : await _db.PayNowTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PaymentReference == reference);

        return View(transaction);
    }

    [AllowAnonymous]
    [HttpGet, HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Declined(string? reference)
    {
        ViewData["Title"] = "Payment not completed";

        reference ??= Request.HasFormContentType ? Request.Form["p2"].ToString() : null;

        if (!string.IsNullOrWhiteSpace(reference))
        {
            var transaction = await _db.PayNowTransactions
                .FirstOrDefaultAsync(p => p.PaymentReference == reference);

            if (transaction is not null && transaction.Status != PaymentStatus.Complete)
            {
                transaction.Status = PaymentStatus.Declined;
                await _db.SaveChangesAsync();
            }

            ViewBag.Reference = reference;
        }

        return View();
    }

    /// <summary>
    /// The server-to-server notification. This is the one that counts.
    /// <para>
    /// GAP - verify the callback before trusting it. The stub does not check anything.
    /// A live implementation must confirm the payload really came from Netcash and that the
    /// amount matches what was requested, then reconcile against the Netcash statement before
    /// anything of value is released.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Notify()
    {
        var form = Request.Form.ToDictionary(k => k.Key, v => v.Value.ToString());
        var reference = form.GetValueOrDefault("p2") ?? form.GetValueOrDefault("Reference");

        _log.LogInformation("Pay Now notify received for {Reference}.", reference);

        if (string.IsNullOrWhiteSpace(reference)) return Ok();

        var transaction = await _db.PayNowTransactions
            .FirstOrDefaultAsync(p => p.PaymentReference == reference);

        if (transaction is null)
        {
            _log.LogWarning("Pay Now notify for unknown reference {Reference}.", reference);
            return Ok(); // Always 200, or Netcash will retry forever.
        }

        var serviceKey = await ResolvePayNowKeyAsync(transaction.TenantId);
        var parsed = await _payNow.ParseCallbackAsync(serviceKey ?? string.Empty, form);

        if (!parsed.Success || parsed.Data is null)
        {
            _log.LogError("Could not parse the Pay Now callback for {Reference}.", reference);
            return Ok();
        }

        var callback = parsed.Data;

        transaction.RawNotifyPayload = System.Text.Json.JsonSerializer.Serialize(form);
        transaction.NetcashTransactionId = callback.NetcashTransactionId;
        transaction.PaymentMethod = callback.Method;
        transaction.ResponseMessage = callback.Reason;

        if (callback.IsAccepted)
        {
            // Guard against a callback that claims a different amount than was requested.
            if (callback.Amount is not null && callback.Amount != transaction.Amount)
            {
                _log.LogError(
                    "Pay Now amount mismatch on {Reference}: expected {Expected}, callback said {Actual}.",
                    reference, transaction.Amount, callback.Amount);

                transaction.Status = PaymentStatus.Declined;
                transaction.ResponseMessage = "Amount mismatch between the request and the callback.";
            }
            else
            {
                transaction.Status = PaymentStatus.Complete;
                transaction.CompletedUtc = DateTime.UtcNow;
            }
        }
        else
        {
            transaction.Status = PaymentStatus.Declined;
        }

        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// Returns a usable Pay Now key, or null. Never a placeholder - posting a customer to
    /// Netcash with a bad service key produces a broken payment page rather than a clear
    /// configuration error, and the customer is the one who sees it.
    /// </summary>
    private async Task<string?> ResolvePayNowKeyAsync(Guid tenantId)
    {
        var capability = await _capabilities.GetAsync(tenantId, NetcashServiceId.PayNow);
        return capability.IsUsable ? capability.ServiceKey : null;
    }
}
