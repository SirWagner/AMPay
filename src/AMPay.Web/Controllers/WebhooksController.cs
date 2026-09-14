using System.Text.Json;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Receives NetConnector postbacks from Netcash.
/// <para>
/// This is how a TT2 or delayed TT1 mandate ever reaches a final state - the batch upload
/// only returns a file token, and the bank answers here, minutes or hours later.
/// </para>
/// <para>
/// The postback URL is configured once on the Netcash NetConnector page and must be publicly
/// reachable over HTTPS. For local development, tunnel it (dev tunnels, ngrok) rather than
/// pointing Netcash at localhost.
/// </para>
/// </summary>
[AllowAnonymous]
[Route("webhooks/netcash")]
public class WebhooksController : Controller
{
    private readonly AppDbContext _db;
    private readonly ILogger<WebhooksController> _log;

    public WebhooksController(AppDbContext db, ILogger<WebhooksController> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// DebiCheck authentication result.
    /// <para>
    /// GAP - this endpoint is unauthenticated. Before go-live, restrict it: allowlist the
    /// Netcash source IP ranges at the firewall or App Service level, and put a shared secret
    /// in the configured postback URL path. Anyone who can reach this URL can currently mark
    /// a mandate authenticated.
    /// </para>
    /// </summary>
    [HttpPost("debicheck")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> DebiCheck([FromBody] DebiCheckPostback? postback)
    {
        if (postback is null)
        {
            _log.LogWarning("DebiCheck postback received with an unreadable body.");
            return Ok();
        }

        _log.LogInformation(
            "DebiCheck postback: reference {AccountReference}, contract {ContractReference}, " +
            "process {Process}, status {Status}.",
            postback.AccountReference, postback.ContractReference, postback.Process, postback.Status);

        // Match on the contract reference where we have one, since the account reference is
        // only unique within a tenant.
        var mandate = await _db.Mandates
            .Include(m => m.Events)
            .FirstOrDefaultAsync(m =>
                (postback.ContractReference != null && m.ContractReference == postback.ContractReference)
                || m.AccountReference == postback.AccountReference);

        if (mandate is null)
        {
            _log.LogWarning("DebiCheck postback did not match any mandate: {Reference}.",
                postback.AccountReference);
            return Ok(); // Always 200, or Netcash retries.
        }

        var from = mandate.Status;
        var accepted = string.Equals(postback.Status, "Accepted", StringComparison.OrdinalIgnoreCase);

        mandate.ContractReference ??= postback.ContractReference;
        mandate.RmsApplied = postback.RMS;

        switch (postback.Process?.ToLowerInvariant())
        {
            case "cancellation":
                mandate.Status = accepted ? MandateStatus.Cancelled : mandate.Status;
                if (accepted) mandate.CancelledUtc = DateTime.UtcNow;
                break;

            case "amendment":
                mandate.Status = accepted ? MandateStatus.Amended : mandate.Status;
                break;

            default: // Initiation
                if (accepted)
                {
                    mandate.Status = MandateStatus.Authenticated;
                    mandate.AuthenticatedUtc = postback.Timestamp ?? DateTime.UtcNow;
                }
                else
                {
                    mandate.Status = MandateStatus.Rejected;
                }
                break;
        }

        // DbSet.Add, not mandate.Events.Add - these entities carry their own Guid key, and
        // attaching through the navigation would make EF emit an UPDATE for a row that has
        // never been inserted.
        _db.MandateEvents.Add(new MandateEvent
        {
            MandateId = mandate.Id,
            EventType = "PostbackReceived",
            FromStatus = from,
            ToStatus = mandate.Status,
            Detail = $"{postback.Process ?? "Initiation"} {postback.Status}.",
            RawPayload = JsonSerializer.Serialize(postback),
            OccurredUtc = postback.Timestamp ?? DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// Netcash eMandate signature postback (form POST, not JSON).
    /// <para>
    /// GAP - not wired to a mandate record yet. The eMandate route (AddMandate) is a separate
    /// flow from DebiCheck and is not used by the current onboarding path; this endpoint
    /// exists so the URL can be registered and the payload inspected before that work starts.
    /// </para>
    /// </summary>
    [HttpPost("emandate")]
    [IgnoreAntiforgeryToken]
    public IActionResult EMandate()
    {
        var form = Request.HasFormContentType
            ? Request.Form.ToDictionary(k => k.Key, v => v.Value.ToString())
            : new Dictionary<string, string>();

        _log.LogInformation("eMandate postback received with {Count} field(s): {Fields}",
            form.Count, string.Join(", ", form.Keys));

        return Ok();
    }
}
