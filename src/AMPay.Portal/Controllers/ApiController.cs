using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AMPay.Domain.Portal;
using AMPay.Portal.Data;
using AMPay.Portal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Portal.Controllers;

/// <summary>
/// The API AM-Pay calls. Nothing else may: every request must carry the shared key.
/// <para>
/// AM-Pay pushes lenders and pulls applications, documents and callbacks. The portal never
/// initiates a call to AM-Pay.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1")]
[ServiceFilter(typeof(ApiKeyFilter))]
[EnableRateLimiting(PortalRateLimits.Api)]
public class ApiController : ControllerBase
{
    private readonly PortalDbContext _db;
    private readonly PortalDocumentStore _store;

    public ApiController(PortalDbContext db, PortalDocumentStore store)
    {
        _db = db;
        _store = store;
    }

    // ---------------------------------------------------------------- lenders

    /// <summary>Creates or updates the lender behind a public code. A tenant has one code; a new code moves it.</summary>
    [HttpPut("lenders/{code}")]
    public async Task<IActionResult> PutLender(string code, PortalLenderSync body)
    {
        code = code.Trim().ToUpperInvariant();
        if (!PortalApi.IsValidLenderCode(code)) return BadRequest("Invalid lender code.");

        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == body.TenantId);

        // The code may already belong to a different tenant - refuse rather than hijack it.
        var holder = await _db.Lenders.FirstOrDefaultAsync(l => l.PublicCode == code);
        if (holder is not null && holder.AmpayTenantId != body.TenantId)
            return Conflict("That code belongs to another lender.");

        if (lender is null)
        {
            lender = new Lender { AmpayTenantId = body.TenantId };
            _db.Lenders.Add(lender);
        }

        lender.PublicCode = code;
        lender.Name = body.Name;
        lender.TradingName = body.TradingName;
        lender.NcrNumber = body.NcrNumber;
        lender.ContactNumber = body.ContactNumber;
        lender.ContactEmail = body.ContactEmail;
        lender.WhatsAppNumber = PortalApi.NormaliseMobile(body.WhatsAppNumber);
        lender.IsActive = body.IsActive;
        lender.PackagesJson = JsonSerializer.Serialize(body.Packages);
        lender.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // The logo travels with every sync; null means the lender removed it.
        var existing = await _db.LenderLogos.FirstOrDefaultAsync(l => l.LenderId == lender.Id);
        if (string.IsNullOrEmpty(body.LogoBase64))
        {
            if (existing is not null) _db.LenderLogos.Remove(existing);
            lender.LogoUpdatedUtc = null;
        }
        else
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(body.LogoBase64); }
            catch (FormatException) { return BadRequest("The logo is not valid base64."); }

            // Checked again here: the portal trusts nothing it is sent, even by AM-Pay.
            var type = AMPay.Domain.Entities.TenantLogo.SniffContentType(bytes);
            if (bytes.Length > AMPay.Domain.Entities.TenantLogo.MaxBytes || type is null)
                return BadRequest("The logo must be a PNG, JPEG or WebP of 300 KB or less.");

            var changed = existing is null || !existing.Data.AsSpan().SequenceEqual(bytes);
            if (existing is null)
            {
                existing = new LenderLogo { LenderId = lender.Id };
                _db.LenderLogos.Add(existing);
            }
            existing.ContentType = type;
            existing.Data = bytes;
            if (changed) lender.LogoUpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- applications

    [HttpGet("applications")]
    public async Task<IReadOnlyList<PortalApplicationSummary>> Applications(Guid tenantId, PortalApplicationStatus? status = null)
    {
        var lender = await _db.Lenders.AsNoTracking().FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return Array.Empty<PortalApplicationSummary>();

        var query = _db.Applications.AsNoTracking().Where(a => a.LenderId == lender.Id);
        query = status is null
            ? query.Where(a => a.Status == PortalApplicationStatus.Submitted || a.Status == PortalApplicationStatus.PickedUp)
            : query.Where(a => a.Status == status);

        var rows = await query
            .OrderByDescending(a => a.SubmittedUtc ?? a.CreatedUtc)
            .Take(200)
            .Select(a => new { a, Docs = a.Documents.Count, Mobile = a.Applicant!.Mobile })
            .ToListAsync();

        return rows.Select(r => Summary(r.a, tenantId, r.Mobile, r.Docs)).ToList();
    }

    [HttpGet("applications/{id:guid}")]
    public async Task<ActionResult<PortalApplicationDetail>> Application(Guid id, Guid tenantId)
    {
        var a = await LoadAsync(id, tenantId);
        if (a is null) return NotFound();

        return new PortalApplicationDetail(
            Summary(a, tenantId, a.Applicant!.Mobile, a.Documents.Count),
            a.Title, a.MiddleNames, a.Email, a.AddressLine1, a.AddressLine2, a.Suburb, a.City, a.Province, a.PostalCode,
            a.EmployerName, a.Occupation, a.EmployedSince, a.PayFrequency, a.SalaryDay,
            a.GrossMonthlyIncome, a.NetMonthlyIncome, a.OtherMonthlyIncome, a.MonthlyExpenses, a.MonthlyDebtRepayments,
            a.PackageName, a.EstimatedInstalment,
            a.DataProcessingConsent, a.CreditCheckConsent, a.MarketingConsent, a.ConsentUtc,
            a.Documents.OrderBy(d => d.UploadedUtc)
                .Select(d => new PortalDocument(d.Id, d.DocumentType, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.UploadedUtc))
                .ToList());
    }

    [HttpGet("applications/{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Document(Guid id, Guid documentId, Guid tenantId)
    {
        var a = await LoadAsync(id, tenantId);
        var doc = a?.Documents.FirstOrDefault(d => d.Id == documentId);
        if (doc is null) return NotFound();

        var stream = _store.Open(doc.StoragePath);
        if (stream is null) return NotFound();
        return File(stream, doc.ContentType ?? "application/octet-stream", doc.FileName);
    }

    /// <summary>AM-Pay has imported the application. The client now sees it as with the lender.</summary>
    [HttpPost("applications/{id:guid}/picked-up")]
    public async Task<IActionResult> PickedUp(Guid id, Guid tenantId, PortalPickedUp body)
    {
        var a = await LoadAsync(id, tenantId, tracking: true);
        if (a is null) return NotFound();
        if (a.Status is not (PortalApplicationStatus.Submitted or PortalApplicationStatus.PickedUp))
            return Conflict("Only a submitted application can be picked up.");

        a.Status = PortalApplicationStatus.PickedUp;
        a.PickedUpUtc ??= DateTime.UtcNow;
        a.AmpayClientId = body.AmpayClientId;
        a.AmpayClientNumber = body.ClientNumber;
        a.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- callbacks

    [HttpGet("callbacks")]
    public async Task<IReadOnlyList<PortalCallback>> Callbacks(Guid tenantId, bool open = true)
    {
        var lender = await _db.Lenders.AsNoTracking().FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return Array.Empty<PortalCallback>();

        return await _db.Callbacks.AsNoTracking()
            .Where(c => c.LenderId == lender.Id && (!open || c.HandledUtc == null))
            .OrderByDescending(c => c.CreatedUtc)
            .Take(200)
            .Select(c => new PortalCallback(c.Id, tenantId, c.ApplicationId, c.Name, c.Mobile, c.PreferredTime, c.Message, c.CreatedUtc, c.HandledUtc))
            .ToListAsync();
    }

    [HttpPost("callbacks/{id:guid}/handled")]
    public async Task<IActionResult> CallbackHandled(Guid id, Guid tenantId)
    {
        var c = await _db.Callbacks.Include(x => x.Lender).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null || c.Lender!.AmpayTenantId != tenantId) return NotFound();

        c.HandledUtc ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- client accounts

    /// <summary>
    /// Creates or replaces one client's account view: loans, schedule and payments. AM-Pay
    /// sends the whole statement each time, so nothing here is ever patched.
    /// </summary>
    [HttpPut("clients/{clientId:guid}")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> PutClient(Guid clientId, Guid tenantId, PortalClientStatement body)
    {
        if (body.AmpayClientId != clientId || body.TenantId != tenantId) return BadRequest("The statement is for a different client.");

        var mobile = PortalApi.NormaliseMobile(body.Mobile);
        if (mobile is null) return BadRequest("The client has no cell number to sign in with.");
        if (body.IdNumberHash is not { Length: 64 }) return BadRequest("The ID number fingerprint is missing.");

        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return NotFound("This lender has no self-service link yet.");

        var account = await _db.ClientAccounts.FirstOrDefaultAsync(a => a.LenderId == lender.Id && a.AmpayClientId == clientId);
        if (account is null)
        {
            account = new ClientAccount { LenderId = lender.Id, AmpayClientId = clientId };
            _db.ClientAccounts.Add(account);
        }

        // A statement older than the one held arrives late: keep the newer.
        if (account.GeneratedUtc > body.GeneratedUtc) return NoContent();

        account.ClientNumber = body.ClientNumber;
        account.FullName = body.FullName;
        account.Mobile = mobile;
        account.IdNumberHash = body.IdNumberHash;
        account.StatementJson = JsonSerializer.Serialize(body);
        account.GeneratedUtc = body.GeneratedUtc;
        account.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Stores or replaces one file in the client's account, e.g. a signed agreement (PDF only).</summary>
    [HttpPut("clients/{clientId:guid}/documents/{key}")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<IActionResult> PutClientDocument(Guid clientId, string key, Guid tenantId, PortalDocumentUpload body)
    {
        if (key.Length is 0 or > 80 || !key.All(c => char.IsLetterOrDigit(c) || c == '-')) return BadRequest("Invalid document key.");

        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        var account = lender is null ? null
            : await _db.ClientAccounts.FirstOrDefaultAsync(a => a.LenderId == lender.Id && a.AmpayClientId == clientId);
        if (account is null) return NotFound("Send the client's account first.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(body.Base64); }
        catch (FormatException) { return BadRequest("The file is not valid base64."); }
        if (bytes.Length is 0 or > 10 * 1024 * 1024 || !bytes.AsSpan().StartsWith("%PDF"u8))
            return BadRequest("Only a PDF of 10 MB or less is accepted.");

        var doc = await _db.AccountDocuments.FirstOrDefaultAsync(d => d.ClientAccountId == account.Id && d.Key == key);
        if (doc is null)
        {
            doc = new ClientAccountDocument { ClientAccountId = account.Id, Key = key };
            _db.AccountDocuments.Add(doc);
        }
        doc.Title = body.Title.Length > 200 ? body.Title[..200] : body.Title;
        doc.LoanNumber = body.LoanNumber;
        doc.ContentType = "application/pdf";
        doc.Data = bytes;
        doc.DatedUtc = body.DatedUtc;
        doc.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The lender withdrew the client's access. Anyone linked to it is unlinked.</summary>
    [HttpDelete("clients/{clientId:guid}")]
    public async Task<IActionResult> DeleteClient(Guid clientId, Guid tenantId)
    {
        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return NoContent();

        var account = await _db.ClientAccounts.FirstOrDefaultAsync(a => a.LenderId == lender.Id && a.AmpayClientId == clientId);
        if (account is null) return NoContent();

        await _db.Applicants.Where(a => a.ClientAccountId == account.Id)
            .ForEachAsync(a => { a.ClientAccountId = null; a.AccountLinkedUtc = null; });
        _db.ClientAccounts.Remove(account);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- "I am already a client"

    [HttpGet("link-requests")]
    public async Task<IReadOnlyList<PortalLinkRequest>> LinkRequests(Guid tenantId)
    {
        var lender = await _db.Lenders.AsNoTracking().FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return Array.Empty<PortalLinkRequest>();

        return await _db.LinkRequests.AsNoTracking()
            .Where(r => r.LenderId == lender.Id && r.Status == LinkRequestStatus.Pending)
            .OrderBy(r => r.CreatedUtc)
            .Take(200)
            .Select(r => new PortalLinkRequest(r.Id, tenantId, r.Mobile, r.Email, r.FullName, r.CreatedUtc, r.IdNumber))
            .ToListAsync();
    }

    /// <summary>The lender confirmed who this is. AM-Pay has already sent that client's account.</summary>
    [HttpPost("link-requests/{id:guid}/approve")]
    public async Task<IActionResult> ApproveLink(Guid id, Guid tenantId, PortalLinkApproval body)
    {
        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        var request = lender is null ? null : await _db.LinkRequests.FirstOrDefaultAsync(r => r.Id == id && r.LenderId == lender.Id);
        if (request is null) return NotFound();

        var account = await _db.ClientAccounts.FirstOrDefaultAsync(a => a.LenderId == lender!.Id && a.AmpayClientId == body.AmpayClientId);
        if (account is null) return Conflict("Send the client's account to the portal before approving.");

        var applicant = await _db.Applicants.FirstOrDefaultAsync(a => a.Id == request.ApplicantId && a.LenderId == lender!.Id);
        if (applicant is null) return NotFound();

        applicant.ClientAccountId = account.Id;
        applicant.AccountLinkedUtc = DateTime.UtcNow;
        applicant.LinkApprovedByLender = true;
        applicant.FailedIdAttempts = 0;
        request.Status = LinkRequestStatus.Approved;
        request.DecidedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("link-requests/{id:guid}/reject")]
    public async Task<IActionResult> RejectLink(Guid id, Guid tenantId)
    {
        var lender = await _db.Lenders.FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        var request = lender is null ? null : await _db.LinkRequests.FirstOrDefaultAsync(r => r.Id == id && r.LenderId == lender.Id);
        if (request is null) return NotFound();

        request.Status = LinkRequestStatus.Rejected;
        request.DecidedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- funnel

    /// <summary>Counts for the lender's dashboard: link opens, applications started and submitted.</summary>
    [HttpGet("funnel")]
    public async Task<PortalFunnel> Funnel(Guid tenantId, DateTime fromUtc, DateTime toUtc)
    {
        var lender = await _db.Lenders.AsNoTracking().FirstOrDefaultAsync(l => l.AmpayTenantId == tenantId);
        if (lender is null) return new PortalFunnel(0, 0, 0);

        var opened = await _db.LinkEvents.CountAsync(e =>
            e.LenderId == lender.Id && e.Kind == LinkEventKind.Opened && e.OccurredUtc >= fromUtc && e.OccurredUtc < toUtc);
        var started = await _db.Applications.CountAsync(a =>
            a.LenderId == lender.Id && a.CreatedUtc >= fromUtc && a.CreatedUtc < toUtc);
        var submitted = await _db.Applications.CountAsync(a =>
            a.LenderId == lender.Id && a.SubmittedUtc >= fromUtc && a.SubmittedUtc < toUtc);

        return new PortalFunnel(opened, started, submitted);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Loads an application only through the tenant that owns it: a tenant id is a scope, not a hint.</summary>
    private async Task<LoanApplication?> LoadAsync(Guid id, Guid tenantId, bool tracking = false)
    {
        var q = _db.Applications.Include(a => a.Documents).Include(a => a.Applicant).Include(a => a.Lender).AsQueryable();
        if (!tracking) q = q.AsNoTracking();
        var a = await q.FirstOrDefaultAsync(x => x.Id == id);
        return a is not null && a.Lender!.AmpayTenantId == tenantId && a.Status != PortalApplicationStatus.Draft ? a : null;
    }

    private static PortalApplicationSummary Summary(LoanApplication a, Guid tenantId, string mobile, int docs) =>
        new(a.Id, a.Reference, tenantId, a.Status, a.FirstName, a.Surname, a.IdNumber, mobile,
            a.RequestedAmount, a.RequestedTerm, docs, a.CreatedUtc, a.SubmittedUtc, a.PickedUpUtc, a.AmpayClientId);
}

/// <summary>Rejects any API call without the shared key. Constant-time comparison.</summary>
public class ApiKeyFilter : IAsyncActionFilter
{
    private readonly PortalOptions _options;
    public ApiKeyFilter(IOptions<PortalOptions> options) => _options = options.Value;

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var presented = context.HttpContext.Request.Headers[PortalApi.ApiKeyHeader].ToString();
        var expected = _options.ApiKey;

        var ok = !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(presented) &&
                 CryptographicOperations.FixedTimeEquals(
                     SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
                     SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

        if (!ok)
        {
            context.Result = new UnauthorizedResult();
            return Task.CompletedTask;
        }

        return next();
    }
}
