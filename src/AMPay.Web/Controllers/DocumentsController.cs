using AMPay.Domain.Documents;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
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
/// Supporting documents: upload, retrieval and verification.
/// <para>
/// Two audiences with deliberately different rights. A capturer uploads and replaces files
/// but cannot pass them. A reviewer passes or fails them but cannot capture. The separation
/// is the whole point of the control - see <see cref="AppRoles.Reviewer"/>.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.CanViewClients)]
public class DocumentsController : Controller
{
    /// <summary>Without these on file a client cannot be onboarded.</summary>
    public static readonly IReadOnlyList<DocumentType> Required = new[]
    {
        DocumentType.IdDocument,
        DocumentType.Payslip
    };

    private readonly AppDbContext _db;
    private readonly IDocumentStore _store;
    private readonly ICurrentTenant _tenant;
    private readonly UserManager<ApplicationUser> _users;
    private readonly DocumentStorageOptions _options;
    private readonly ILogger<DocumentsController> _log;

    public DocumentsController(
        AppDbContext db,
        IDocumentStore store,
        ICurrentTenant tenant,
        UserManager<ApplicationUser> users,
        IOptions<DocumentStorageOptions> options,
        ILogger<DocumentsController> log)
    {
        _db = db;
        _store = store;
        _tenant = tenant;
        _users = users;
        _options = options.Value;
        _log = log;
    }

    // ---------------------------------------------------------------- the review queue

    /// <summary>
    /// Everything waiting on a reviewer, oldest first. This is a reviewer's home page.
    /// </summary>
    [Authorize(Policy = AppPolicies.CanReviewDocuments)]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Document review";
        await _tenant.LoadAsync();

        var query = _db.ClientDocuments.AsNoTracking()
            .Include(d => d.Client)
            .Where(d => d.ReviewStatus == DocumentReviewStatus.Pending);

        if (!_tenant.IsPlatformUser || _tenant.TenantId is not null)
            query = query.Where(d => d.Client!.TenantId == _tenant.TenantId);

        var pending = await query
            .OrderBy(d => d.UploadedUtc)
            .Select(d => new DocumentQueueRow
            {
                DocumentId = d.Id,
                ClientId = d.ClientId,
                ClientNumber = d.Client!.ClientNumber,
                ClientName = d.Client.FirstName + " " + d.Client.Surname,
                DocumentType = d.DocumentType,
                FileName = d.FileName,
                SizeBytes = d.SizeBytes,
                UploadedUtc = d.UploadedUtc
            })
            .ToListAsync();

        return View(pending);
    }

    // ---------------------------------------------------------------- one client's file

    [HttpGet]
    public async Task<IActionResult> Client(Guid id)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        ViewData["Title"] = $"Documents · {client.FullName}";

        return View(await BuildClientModelAsync(client));
    }

    // ---------------------------------------------------------------- upload

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, DocumentType documentType, IFormFile? file)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Client), new { id });
        }

        // Content type is a client-supplied header, so it is a courtesy check only - the
        // extension allow-list in the store is what actually constrains what lands on disk.
        if (!string.IsNullOrEmpty(file.ContentType) &&
            !_options.AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            TempData["Error"] =
                $"{file.ContentType} is not an accepted document type. Upload a PDF or a photograph.";
            return RedirectToAction(nameof(Client), new { id });
        }

        StoredDocument stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await _store.SaveAsync(client.TenantId, client.Id, file.FileName, content);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Client), new { id });
        }

        var document = new ClientDocument
        {
            ClientId = client.Id,
            DocumentType = documentType,
            FileName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType,
            SizeBytes = stored.SizeBytes,
            StoragePath = stored.StoragePath,
            ContentHash = stored.ContentHash,
            UploadedByUserId = _users.GetUserId(User),
            ReviewStatus = DocumentReviewStatus.Pending
        };

        _db.ClientDocuments.Add(document);

        // Uploading the last outstanding document is what moves a client into the queue.
        await MoveToPendingVerificationIfReadyAsync(client);

        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Document {Type} uploaded for client {ClientNumber}.", documentType, client.ClientNumber);

        TempData["Success"] = $"{Describe(documentType)} uploaded and sent for verification.";
        return RedirectToAction(nameof(Client), new { id });
    }

    // ---------------------------------------------------------------- retrieval

    /// <summary>
    /// Streams a stored document. Never a redirect to storage: the authorisation check and
    /// the bytes must travel together, or the check is decorative.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Download(Guid id, bool inline = true)
    {
        var document = await _db.ClientDocuments
            .Include(d => d.Client)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (document?.Client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(document.Client.TenantId);

        var stream = await _store.OpenAsync(document.StoragePath);
        if (stream is null)
        {
            _log.LogError(
                "Document {DocumentId} is recorded but missing from the store at {Path}.",
                document.Id, document.StoragePath);
            return NotFound();
        }

        var contentType = document.ContentType ?? "application/octet-stream";

        // Inline so a reviewer reads the ID in the browser instead of littering their
        // downloads folder with copies of other people's identity documents.
        return inline
            ? File(stream, contentType)
            : File(stream, contentType, document.FileName);
    }

    // ---------------------------------------------------------------- verification

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanReviewDocuments)]
    public async Task<IActionResult> Review(Guid id, DocumentReviewStatus outcome, string? notes, string? returnTo)
    {
        var document = await _db.ClientDocuments
            .Include(d => d.Client)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (document?.Client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(document.Client.TenantId);

        if (outcome == DocumentReviewStatus.Pending)
        {
            TempData["Error"] = "Choose whether the document is accepted or rejected.";
            return Back(document.ClientId, returnTo);
        }

        // A rejection the capturer cannot act on is just an obstacle.
        if (outcome == DocumentReviewStatus.Rejected && string.IsNullOrWhiteSpace(notes))
        {
            TempData["Error"] = "Say why the document was rejected so it can be corrected.";
            return Back(document.ClientId, returnTo);
        }

        document.ReviewStatus = outcome;
        document.ReviewedUtc = DateTime.UtcNow;
        document.ReviewedByUserId = _users.GetUserId(User);
        document.ReviewNotes = notes;

        await _db.SaveChangesAsync();

        await ApplyVerificationOutcomeAsync(document.ClientId);
        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Document {DocumentId} for client {ClientId} marked {Outcome}.",
            document.Id, document.ClientId, outcome);

        TempData["Success"] = outcome == DocumentReviewStatus.Approved
            ? "Document accepted."
            : "Document rejected and returned to the capturer.";

        return Back(document.ClientId, returnTo);
    }

    // ---------------------------------------------------------------- removal

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Remove(Guid id)
    {
        var document = await _db.ClientDocuments
            .Include(d => d.Client)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (document?.Client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(document.Client.TenantId);

        // An approved document is evidence that a decision was taken on it. Withdrawing it
        // would leave the onboarding unexplained.
        if (document.ReviewStatus == DocumentReviewStatus.Approved)
        {
            TempData["Error"] =
                "An accepted document cannot be removed. Upload a replacement instead.";
            return RedirectToAction(nameof(Client), new { id = document.ClientId });
        }

        var clientId = document.ClientId;

        await _store.DeleteAsync(document.StoragePath);
        _db.ClientDocuments.Remove(document);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document removed.";
        return RedirectToAction(nameof(Client), new { id = clientId });
    }

    // ---------------------------------------------------------------- helpers

    private IActionResult Back(Guid clientId, string? returnTo) =>
        string.Equals(returnTo, "queue", StringComparison.OrdinalIgnoreCase)
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Client), new { id = clientId });

    private async Task<Client?> LoadClientAsync(Guid id)
    {
        var client = await _db.Clients
            .Include(c => c.Documents)
            .Include(c => c.Employment)
            .Include(c => c.Financial)
            .Include(c => c.Addresses)
            .Include(c => c.BankAccounts)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (client is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        return client;
    }

    private async Task<ClientDocumentsModel> BuildClientModelAsync(Client client)
    {
        var reviewerIds = client.Documents
            .Select(d => d.ReviewedByUserId)
            .Concat(client.Documents.Select(d => d.UploadedByUserId))
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        var names = await _db.Users
            .Where(u => reviewerIds.Contains(u.Id.ToString()))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.FullName ?? u.Email ?? "Unknown");

        string? Name(string? userId) =>
            userId is not null && names.TryGetValue(userId, out var n) ? n : null;

        var primaryAccount = client.BankAccounts.FirstOrDefault(a => a.IsPrimary)
                             ?? client.BankAccounts.FirstOrDefault();

        return new ClientDocumentsModel
        {
            ClientId = client.Id,
            ClientNumber = client.ClientNumber,
            ClientName = client.FullName,
            ClientStatus = client.Status,
            CanReview = User.IsInRole(AppRoles.Reviewer) ||
                        User.IsInRole(AppRoles.TenantAdmin) ||
                        User.IsInRole(AppRoles.SuperAdmin),
            CanOpenClientFile = User.IsInRole(AppRoles.Capturer) ||
                                User.IsInRole(AppRoles.TenantAdmin) ||
                                User.IsInRole(AppRoles.SuperAdmin),

            IdNumber = client.IdNumber,
            IsSaIdNumber = client.IsSaIdNumber,
            DateOfBirth = client.DateOfBirth,
            MobileNumber = client.Addresses
                .Select(a => a.MobileNumber)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)),

            EmployerName = client.Employment?.EmployerName,
            Occupation = client.Employment?.Occupation,
            GrossMonthlyIncome = client.Financial?.GrossMonthlyIncome,
            NetMonthlyIncome = client.Financial?.NetMonthlyIncome,

            BankAccountHolder = primaryAccount?.AccountHolderName,
            BankName = primaryAccount?.BankName,
            MaskedAccountNumber = primaryAccount?.MaskedAccountNumber,

            MaxFileSizeBytes = _options.MaxFileSizeBytes,
            AcceptedExtensions = string.Join(", ", _options.AllowedExtensions),
            Documents = client.Documents
                .OrderBy(d => d.DocumentType)
                .ThenByDescending(d => d.UploadedUtc)
                .Select(d => new ClientDocumentsModel.Row
                {
                    Id = d.Id,
                    DocumentType = d.DocumentType,
                    FileName = d.FileName,
                    SizeBytes = d.SizeBytes,
                    UploadedUtc = d.UploadedUtc,
                    UploadedBy = Name(d.UploadedByUserId),
                    ReviewStatus = d.ReviewStatus,
                    ReviewedUtc = d.ReviewedUtc,
                    ReviewedBy = Name(d.ReviewedByUserId),
                    ReviewNotes = d.ReviewNotes
                })
                .ToList(),
            Outstanding = Required
                .Where(t => !client.Documents.Any(d =>
                    d.DocumentType == t && d.ReviewStatus != DocumentReviewStatus.Rejected))
                .ToList()
        };
    }

    /// <summary>
    /// Moves a captured client into the verification queue once every required document is
    /// on file. Does not save - the caller owns the transaction.
    /// </summary>
    private Task MoveToPendingVerificationIfReadyAsync(Client client)
    {
        if (client.Status is not (ClientStatus.Draft or ClientStatus.Captured))
            return Task.CompletedTask;

        client.Status = ClientStatus.PendingVerification;
        client.UpdatedUtc = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-derives a client's status from the state of their documents after a review.
    /// <para>
    /// Deliberately recomputed from scratch rather than nudged one step at a time. A
    /// reviewer who rejects a document they previously accepted must pull the client back
    /// out of Onboarded, and step-wise transitions quietly fail to do that.
    /// </para>
    /// </summary>
    private async Task ApplyVerificationOutcomeAsync(Guid clientId)
    {
        var client = await _db.Clients
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == clientId);

        if (client is null) return;

        // Never disturb a client who is already trading or whose file is closed.
        if (client.Status is ClientStatus.Active or ClientStatus.Suspended or ClientStatus.Closed)
            return;

        var allRequiredApproved = Required.All(t =>
            client.Documents.Any(d =>
                d.DocumentType == t && d.ReviewStatus == DocumentReviewStatus.Approved));

        var anyRejected = client.Documents.Any(d =>
            d.ReviewStatus == DocumentReviewStatus.Rejected);

        var newStatus = allRequiredApproved
            ? ClientStatus.Onboarded
            : anyRejected
                ? ClientStatus.Captured   // back to the capturer to fix
                : ClientStatus.PendingVerification;

        if (client.Status == newStatus) return;

        client.Status = newStatus;
        client.UpdatedUtc = DateTime.UtcNow;

        _log.LogInformation(
            "Client {ClientNumber} moved to {Status} on document review.",
            client.ClientNumber, newStatus);
    }

    internal static string Describe(DocumentType type) => type switch
    {
        DocumentType.IdDocument => "Identity document",
        DocumentType.Payslip => "Payslip",
        DocumentType.ProofOfBankAccount => "Proof of bank account",
        DocumentType.ProofOfAddress => "Proof of address",
        DocumentType.Combined => "Combined supporting pack",
        DocumentType.SignedMandate => "Signed mandate",
        _ => "Other document"
    };
}
