using System.Security.Cryptography;
using System.Text;
using AMPay.Domain.Contracts;
using AMPay.Domain.Credit;
using AMPay.Domain.Documents;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Contracts;

/// <summary>A refusal the person on screen can act on. The message is shown as is.</summary>
public class ContractException : Exception
{
    public ContractException(string message) : base(message) { }
}

/// <summary>
/// The life of a contract pack: issue, send, view, sign (online or on paper), void.
/// <para>
/// Authorisation is the caller's job - this service trusts that the controller has checked
/// the tenant. What it guards is the state machine and the evidence: a pack is frozen when
/// issued, its fingerprint is checked every time it is rendered, and a signature is only
/// recorded against the exact pack the client was shown.
/// </para>
/// </summary>
public class ContractService
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PinResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxPinAttempts = 5;

    private readonly AppDbContext _db;
    private readonly IAffordabilityService _affordability;
    private readonly IContractRenderer _renderer;
    private readonly IMessageSender _messages;
    private readonly IDocumentStore _store;
    private readonly ILogger<ContractService> _log;

    public ContractService(
        AppDbContext db,
        IAffordabilityService affordability,
        IContractRenderer renderer,
        IMessageSender messages,
        IDocumentStore store,
        ILogger<ContractService> log)
    {
        _db = db;
        _affordability = affordability;
        _renderer = renderer;
        _messages = messages;
        _store = store;
        _log = log;
    }

    public bool MessagesAreStubbed => _messages.IsStubbed;

    // ---------------------------------------------------------------- templates

    /// <summary>Adds the draft wording for any section a lender does not have yet. Never overwrites.</summary>
    public async Task EnsureTemplatesAsync(Guid tenantId, CancellationToken ct = default)
    {
        var have = await _db.ContractTemplates
            .Where(t => t.TenantId == tenantId)
            .Select(t => t.Kind)
            .ToListAsync(ct);

        var missing = ContractTemplateDefaults.MissingFor(tenantId, have);
        if (missing.Count == 0) return;

        _db.ContractTemplates.AddRange(missing);
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- issue

    /// <summary>
    /// Freezes the loan as it stands into a new pack. Any pack still open on the loan is
    /// voided: there is only ever one pack a client can sign.
    /// </summary>
    public async Task<LoanContract> IssueAsync(Guid loanId, string? userId, CancellationToken ct = default)
    {
        var loan = await _db.Loans
            .Include(l => l.CreditPackage)
            .Include(l => l.Schedule)
            .FirstOrDefaultAsync(l => l.Id == loanId, ct)
            ?? throw new ContractException("The loan was not found.");

        if (loan.Status != LoanStatus.Approved)
            throw new ContractException("A contract can be issued only for an approved loan that has not been paid out.");

        var client = await _db.Clients
            .AsSplitQuery()
            .Include(c => c.Employment)
            .Include(c => c.Financial)
            .Include(c => c.Addresses)
            .Include(c => c.BankAccounts)
            .Include(c => c.Budgets)
            .FirstAsync(c => c.Id == loan.ClientId, ct);

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == loan.TenantId, ct);

        await EnsureTemplatesAsync(tenant.Id, ct);
        var templates = await _db.ContractTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenant.Id)
            .ToDictionaryAsync(t => t.Kind, ct);

        var own = await _db.Loans.AsNoTracking()
            .Where(l => l.ClientId == loan.ClientId && l.Id != loan.Id)
            .Where(l => l.Status == LoanStatus.Approved || l.Status == LoanStatus.Disbursed)
            .SumAsync(l => (decimal?)l.FirstInstalment, ct) ?? 0m;

        var input = LoanOrigination.AffordabilityInputFor(client.Financial, loan.FirstInstalment, own);
        (AffordabilityInput, AffordabilityResult)? budget =
            input is null ? null : (input, _affordability.Assess(input));

        var previous = await _db.LoanContracts
            .Where(c => c.LoanId == loan.Id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var open in previous.Where(c => c.IsOpen))
            Void(open, "Replaced by a newly issued contract.", userId, now);

        var issue = previous.Count == 0 ? 1 : previous.Max(c => c.Issue) + 1;
        var reference = $"{loan.LoanNumber}/{issue}";

        var snapshot = ContractSnapshotBuilder.Build(new ContractSnapshotBuilder.Inputs(
            loan, client, tenant, templates, budget, reference, now));

        var json = snapshot.ToJson();

        var contract = new LoanContract
        {
            TenantId = loan.TenantId,
            LoanId = loan.Id,
            ClientId = client.Id,
            Reference = reference,
            Issue = issue,
            Status = ContractStatus.Issued,
            SnapshotJson = json,
            SnapshotHash = ContractSnapshot.HashOf(json),
            TemplatesApproved = snapshot.TemplatesApproved,
            CreatedUtc = now,
            CreatedByUserId = userId
        };

        _db.LoanContracts.Add(contract);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Contract {Reference} issued{Draft}.",
            reference, snapshot.TemplatesApproved ? "" : " with draft wording");

        return contract;
    }

    // ---------------------------------------------------------------- send

    public record SendOutcome(string Link, IReadOnlyList<string> SentTo, IReadOnlyList<string> Skipped, bool Stubbed);

    /// <summary>
    /// Creates a fresh signing link - any earlier link stops working - and sends it by the
    /// chosen channels. The link is returned so staff can also hand it over another way.
    /// </summary>
    public async Task<SendOutcome> SendAsync(
        Guid contractId, bool bySms, bool byEmail, string publicBaseUrl, string? userId, CancellationToken ct = default)
    {
        var contract = await _db.LoanContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
                       ?? throw new ContractException("The contract was not found.");

        if (!contract.IsOpen)
            throw new ContractException("Only a contract that is not yet signed or voided can be sent.");

        EnsureWordingMaySign(contract);

        if (!bySms && !byEmail)
            throw new ContractException("Choose SMS, email or both.");

        var snapshot = ReadSnapshot(contract);
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == contract.TenantId, ct);
        var lender = tenant.TradingName ?? tenant.Name;

        var token = NewToken();
        var now = DateTime.UtcNow;
        contract.AccessTokenHash = Hash(token);
        contract.AccessExpiresUtc = now + LinkLifetime;

        var link = $"{publicBaseUrl.TrimEnd('/')}/sign/{token}";
        var sentTo = new List<string>();
        var skipped = new List<string>();
        var context = $"Contract {contract.Reference}";

        if (bySms)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Borrower.Mobile))
                skipped.Add("SMS: no cell number on file");
            else
            {
                await _messages.SendAsync(new OutgoingMessage(
                    MessageChannel.Sms, snapshot.Borrower.Mobile,
                    $"{lender}: your credit agreement {contract.Reference} is ready to read and sign: {link} " +
                    $"The link expires on {ContractFormat.Date(ContractFormat.ToSast(contract.AccessExpiresUtc.Value))}.",
                    TenantId: contract.TenantId, Context: context,
                    AuditBody: $"{lender}: your credit agreement {contract.Reference} is ready to read and sign: [link]"), ct);
                sentTo.Add("SMS");
            }
        }

        if (byEmail)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Borrower.Email))
                skipped.Add("Email: no email address on file");
            else
            {
                await _messages.SendAsync(new OutgoingMessage(
                    MessageChannel.Email, snapshot.Borrower.Email,
                    $"Dear {snapshot.Borrower.FullName} {snapshot.Borrower.Surname},\n\n" +
                    $"Your credit agreement {contract.Reference} with {lender} is ready. It includes your quote, " +
                    "payment schedule, budget and DebiCheck debit order mandate.\n\n" +
                    $"Read it, download a copy and sign it online here:\n{link}\n\n" +
                    $"The link expires on {ContractFormat.Date(ContractFormat.ToSast(contract.AccessExpiresUtc.Value))}. " +
                    "You will need your ID number and the cell phone the PIN is sent to.\n\n" +
                    $"If you did not apply for credit with {lender}, do not sign, and contact us on {tenant.ContactNumber}.\n\n{lender}",
                    Subject: $"Your credit agreement {contract.Reference}",
                    TenantId: contract.TenantId, Context: context,
                    AuditBody: $"Contract {contract.Reference} signing link sent (link withheld from the audit copy)."), ct);
                sentTo.Add("email");
            }
        }

        if (sentTo.Count == 0)
            throw new ContractException($"Nothing was sent. {string.Join("; ", skipped)}. Capture the contact details and issue the contract again.");

        contract.Status = contract.Status == ContractStatus.Viewed ? ContractStatus.Viewed : ContractStatus.Sent;
        contract.SentUtc = now;
        contract.SentByUserId = userId;
        contract.SentChannels = string.Join(",", sentTo);

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Contract {Reference} sent by {Channels}.", contract.Reference, contract.SentChannels);

        return new SendOutcome(link, sentTo, skipped, _messages.IsStubbed);
    }

    // ---------------------------------------------------------------- the client's side

    /// <summary>The pack a signing link opens, or null when the link is wrong, expired or withdrawn.</summary>
    public async Task<LoanContract?> FindByTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;

        var hash = Hash(token);
        var contract = await _db.LoanContracts.FirstOrDefaultAsync(c => c.AccessTokenHash == hash, ct);

        if (contract is null || contract.Status == ContractStatus.Voided) return null;
        if (contract.AccessExpiresUtc is null || contract.AccessExpiresUtc < DateTime.UtcNow) return null;

        return contract;
    }

    public async Task RecordViewAsync(LoanContract contract, CancellationToken ct = default)
    {
        if (contract.FirstViewedUtc is not null) return;

        contract.FirstViewedUtc = DateTime.UtcNow;
        if (contract.Status == ContractStatus.Sent) contract.Status = ContractStatus.Viewed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a fresh six-digit PIN to the cell number on the pack. Returns the masked number.</summary>
    public async Task<string> SendPinAsync(LoanContract contract, CancellationToken ct = default)
    {
        if (!contract.IsOpen) throw new ContractException("This agreement can no longer be signed.");

        var snapshot = ReadSnapshot(contract);
        var mobile = snapshot.Borrower.Mobile;

        if (string.IsNullOrWhiteSpace(mobile))
            throw new ContractException(
                "There is no cell number on this agreement, so it cannot be signed online. " +
                "Contact the credit provider to sign it on paper.");

        var now = DateTime.UtcNow;
        if (contract.OtpSentUtc is { } last && now - last < PinResendInterval)
            throw new ContractException("A PIN was sent less than a minute ago. Wait a moment before asking for another.");

        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        contract.OtpHash = PinHash(contract.Id, pin);
        contract.OtpExpiresUtc = now + PinLifetime;
        contract.OtpSentUtc = now;
        contract.OtpFailedAttempts = 0;
        await _db.SaveChangesAsync(ct);

        await _messages.SendAsync(new OutgoingMessage(
            MessageChannel.Sms, mobile,
            $"Your PIN to sign agreement {contract.Reference} is {pin}. It expires in 10 minutes. " +
            "Never share it - the credit provider will not ask you for it.",
            TenantId: contract.TenantId, Context: $"Signing PIN {contract.Reference}",
            AuditBody: $"Your PIN to sign agreement {contract.Reference} is ******."), ct);

        return ContractFormat.MaskMobile(mobile);
    }

    /// <summary>
    /// Records the client's online signature. Every check must pass: the PIN, its expiry,
    /// the attempt limit, the ID number on the pack, and the pack's fingerprint.
    /// </summary>
    public async Task SignOnlineAsync(
        LoanContract contract, string? pin, string? typedName, string? idNumber,
        bool accepted, string? ip, string? userAgent, CancellationToken ct = default)
    {
        if (!contract.IsOpen) throw new ContractException("This agreement can no longer be signed.");
        EnsureWordingMaySign(contract);
        if (!accepted) throw new ContractException("Tick the box to confirm you have read and accept the agreement.");
        if (string.IsNullOrWhiteSpace(typedName)) throw new ContractException("Type your full name as your signature.");

        if (contract.OtpHash is null || contract.OtpExpiresUtc is null)
            throw new ContractException("Ask for a PIN first.");

        if (contract.OtpFailedAttempts >= MaxPinAttempts)
            throw new ContractException("Too many wrong PINs. Ask for a new PIN.");

        if (contract.OtpExpiresUtc < DateTime.UtcNow)
            throw new ContractException("The PIN has expired. Ask for a new one.");

        var snapshot = ReadSnapshot(contract);

        var pinOk = !string.IsNullOrWhiteSpace(pin) &&
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(PinHash(contract.Id, pin.Trim())),
                        Encoding.ASCII.GetBytes(contract.OtpHash));

        var idOk = Digits(idNumber) == Digits(snapshot.Borrower.IdNumber);

        if (!pinOk || !idOk)
        {
            contract.OtpFailedAttempts++;
            if (contract.OtpFailedAttempts >= MaxPinAttempts)
            {
                // Burn the PIN: a new one must be requested, which costs the guesser a minute.
                contract.OtpHash = null;
                contract.OtpExpiresUtc = null;
            }

            await _db.SaveChangesAsync(ct);

            _log.LogWarning("Failed signing attempt {Attempt} on contract {Reference}.",
                contract.OtpFailedAttempts, contract.Reference);

            // One message for both: which of the two was wrong is useful only to an impostor.
            throw new ContractException(contract.OtpFailedAttempts >= MaxPinAttempts
                ? "The PIN or ID number is wrong. Too many attempts - ask for a new PIN."
                : "The PIN or ID number is wrong.");
        }

        contract.Status = ContractStatus.Signed;
        contract.SignatureMethod = SignatureMethod.OnlineOtp;
        contract.SignedUtc = DateTime.UtcNow;
        contract.SignedName = Trim(typedName, 200);
        contract.SignedIdNumber = Digits(idNumber);
        contract.SignedMobile = snapshot.Borrower.Mobile;
        contract.SignedIp = Trim(ip, 64);
        contract.SignedUserAgent = Trim(userAgent, 400);
        contract.OtpHash = null;
        contract.OtpExpiresUtc = null;

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Contract {Reference} signed online.", contract.Reference);
    }

    // ---------------------------------------------------------------- staff side

    /// <summary>Files a scanned copy the client signed on paper and records the pack as signed.</summary>
    public async Task RecordSignedCopyAsync(
        Guid contractId, Stream content, string fileName, string? contentType, string? userId, CancellationToken ct = default)
    {
        var contract = await _db.LoanContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
                       ?? throw new ContractException("The contract was not found.");

        if (!contract.IsOpen)
            throw new ContractException("Only a contract that is not yet signed or voided can be recorded as signed.");

        EnsureWordingMaySign(contract);

        StoredDocument stored;
        try
        {
            stored = await _store.SaveAsync(contract.TenantId, contract.ClientId, fileName, content, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ContractException(ex.Message);
        }

        var document = new ClientDocument
        {
            ClientId = contract.ClientId,
            DocumentType = DocumentType.SignedAgreement,
            FileName = Path.GetFileName(fileName),
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            StoragePath = stored.StoragePath,
            ContentHash = stored.ContentHash,
            UploadedByUserId = userId,
            ReviewStatus = DocumentReviewStatus.Pending
        };
        _db.ClientDocuments.Add(document);

        var snapshot = ReadSnapshot(contract);

        contract.Status = ContractStatus.Signed;
        contract.SignatureMethod = SignatureMethod.UploadedSignedCopy;
        contract.SignedUtc = DateTime.UtcNow;
        contract.SignedName = $"{snapshot.Borrower.FullName} {snapshot.Borrower.Surname}";
        contract.SignedIdNumber = snapshot.Borrower.IdNumber;
        contract.SignedRecordedByUserId = userId;
        contract.SignedCopyDocument = document;

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Contract {Reference} recorded as signed on paper.", contract.Reference);
    }

    public async Task VoidAsync(Guid contractId, string? reason, string? userId, CancellationToken ct = default)
    {
        var contract = await _db.LoanContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
                       ?? throw new ContractException("The contract was not found.");

        if (contract.Status == ContractStatus.Voided)
            throw new ContractException("This contract is already voided.");

        var disbursed = await _db.Loans.AnyAsync(l => l.Id == contract.LoanId && l.Status == LoanStatus.Disbursed, ct);
        if (disbursed && contract.Status == ContractStatus.Signed)
            throw new ContractException("The loan has been paid out on this signed contract. It cannot be voided.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ContractException("Record why the contract is being voided.");

        Void(contract, reason.Trim(), userId, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Voids every open pack on a loan - used when the loan itself is cancelled.</summary>
    public async Task VoidOpenForLoanAsync(Guid loanId, string reason, string? userId, CancellationToken ct = default)
    {
        var open = await _db.LoanContracts
            .Where(c => c.LoanId == loanId && c.Status != ContractStatus.Signed && c.Status != ContractStatus.Voided)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var c in open) Void(c, reason, userId, now);
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Reads the frozen pack back, refusing one whose fingerprint no longer matches.</summary>
    public static ContractSnapshot ReadSnapshot(LoanContract contract)
    {
        if (ContractSnapshot.HashOf(contract.SnapshotJson) != contract.SnapshotHash)
            throw new InvalidOperationException(
                $"Contract {contract.Reference} has been altered since it was issued: its fingerprint does not match.");

        return ContractSnapshot.FromJson(contract.SnapshotJson);
    }

    public static ContractSignatureRecord? SignatureOf(LoanContract c, string? recordedByName) =>
        c.Status != ContractStatus.Signed || c.SignedUtc is null
            ? null
            : new ContractSignatureRecord(
                c.SignatureMethod, c.SignedUtc.Value, c.SignedName ?? "",
                c.SignedIdNumber, ContractFormat.MaskMobile(c.SignedMobile), c.SignedIp,
                c.SignatureMethod == SignatureMethod.UploadedSignedCopy ? recordedByName : null);

    public byte[] RenderPdf(LoanContract contract, string? recordedByName = null) =>
        _renderer.RenderPdf(ReadSnapshot(contract), contract.SnapshotHash, SignatureOf(contract, recordedByName));

    /// <summary>The copy a signing link downloads: ID number masked, as on the page.</summary>
    public byte[] RenderPublicPdf(LoanContract contract) =>
        _renderer.RenderPdf(ReadSnapshot(contract).ForPublicView(), contract.SnapshotHash, PublicSignatureOf(contract));

    public static ContractSignatureRecord? PublicSignatureOf(LoanContract c) =>
        SignatureOf(c, null) is { } s ? s with { SignedIdNumber = ContractFormat.MaskId(s.SignedIdNumber) } : null;

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Draft wording may go out only while messages are stubbed - nothing reaches a real
    /// client then, and the whole flow can be tested before the attorney has signed off.
    /// Once a provider is connected, unapproved wording is never sent or signed.
    /// </summary>
    private void EnsureWordingMaySign(LoanContract contract)
    {
        if (contract.TemplatesApproved || _messages.IsStubbed) return;

        throw new ContractException(
            "This contract uses draft wording. An administrator must approve the contract templates, " +
            "then issue the contract again, before it can be sent or signed.");
    }

    private static void Void(LoanContract c, string reason, string? userId, DateTime now)
    {
        c.Status = ContractStatus.Voided;
        c.VoidedUtc = now;
        c.VoidedByUserId = userId;
        c.VoidReason = Trim(reason, 500);
        c.AccessTokenHash = null;
        c.AccessExpiresUtc = null;
        c.OtpHash = null;
        c.OtpExpiresUtc = null;
    }

    /// <summary>24 random bytes as URL-safe base64: 32 characters, 192 bits.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Only the hash of a link is stored, so a database leak does not leak working links.</summary>
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Salted with the contract, so the same PIN on two packs never hashes alike.</summary>
    private static string PinHash(Guid contractId, string pin) => Hash($"{contractId:N}:{pin}");

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
