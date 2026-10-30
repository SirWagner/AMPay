using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Contracts;

/// <summary>
/// The quote step between approval and agreement: issue, send, view, accept (online with a
/// PIN, or in the branch), decline, expire, void.
/// <para>
/// A quote is binding on the lender for five business days (National Credit Act s92). The
/// agreement can be issued only from an accepted quote, and only while the loan's terms still
/// match it - <see cref="ContractService.IssueAsync"/> checks the fingerprint.
/// </para>
/// </summary>
public class QuoteService
{
    public const int ValidBusinessDays = 5;
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PinResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxPinAttempts = 3;
    public const int MaxPinsPerHour = 3;

    /// <summary>What the client confirms by ticking, in order. Stored word for word on acceptance.</summary>
    public static IReadOnlyList<string> Consents(string lender) => new[]
    {
        "I have read this quote and I understand the total cost of the loan, the fees, the interest and the instalments.",
        "The information I gave about my income, expenses and debts is true and complete.",
        $"I want {lender} to prepare a credit agreement on these terms, with a DebiCheck debit order for the instalments."
    };

    public static IReadOnlyList<string> DeclineReasons { get; } = new[]
    {
        "The loan costs too much",
        "The instalment is too high",
        "I got credit somewhere else",
        "I no longer need the loan",
        "Other"
    };

    private readonly AppDbContext _db;
    private readonly ContractService _contracts;
    private readonly IMessageSender _messages;
    private readonly ILogger<QuoteService> _log;

    public QuoteService(AppDbContext db, ContractService contracts, IMessageSender messages, ILogger<QuoteService> log)
    {
        _db = db;
        _contracts = contracts;
        _messages = messages;
        _log = log;
    }

    public bool MessagesAreStubbed => _messages.IsStubbed;

    /// <summary>The last second of the fifth business day after <paramref name="issuedUtc"/>, South African time.</summary>
    public static DateTime ValidUntil(DateTime issuedUtc) =>
        Sast.StartUtc(SaPublicHolidays.AddBusinessDays(Sast.DateOf(issuedUtc), ValidBusinessDays).AddDays(1)).AddSeconds(-1);

    // ---------------------------------------------------------------- issue

    /// <summary>
    /// Freezes the approved loan's cost of credit into a new quote. Any quote still waiting
    /// for an answer, or accepted but not yet turned into a signed agreement, is voided.
    /// </summary>
    public async Task<CreditQuote> IssueAsync(Guid loanId, string? userId, CancellationToken ct = default)
    {
        var loan = await _db.Loans
            .Include(l => l.CreditPackage)
            .Include(l => l.Schedule)
            .FirstOrDefaultAsync(l => l.Id == loanId, ct)
            ?? throw new ContractException("The loan was not found.");

        if (loan.Status != LoanStatus.Approved)
            throw new ContractException("A quote can be issued only for an approved loan that has not been paid out.");

        var ncr = await _db.Tenants.Where(t => t.Id == loan.TenantId).Select(t => t.NcrNumber).FirstAsync(ct);
        if (string.IsNullOrWhiteSpace(ncr))
            throw new ContractException("Record the business's NCR registration number before issuing a quote.");

        if (await _db.LoanContracts.AnyAsync(c => c.LoanId == loan.Id && c.Status == ContractStatus.Signed, ct))
            throw new ContractException("The client has already signed the agreement for this loan.");

        var previous = await _db.CreditQuotes.Where(q => q.LoanId == loan.Id).ToListAsync(ct);
        var issue = previous.Count == 0 ? 1 : previous.Max(q => q.Issue) + 1;
        var reference = $"{loan.LoanNumber}/Q{issue}";
        var now = DateTime.UtcNow;

        var snapshot = await _contracts.BuildSnapshotAsync(loan, reference, now, ct);
        snapshot = snapshot with { Quote = snapshot.Quote with { QuoteDate = Sast.DateOf(now) } };

        foreach (var q in previous.Where(q => q.IsOpen || q.Status == QuoteStatus.Accepted))
            Void(q, "Replaced by a newly issued quote.", userId, now);

        // An unsigned agreement carries the old quote's terms: it goes too.
        await _contracts.VoidOpenForLoanAsync(loan.Id, "Replaced by a newly issued quote.", userId, ct);

        var json = snapshot.ToJson();
        var quote = new CreditQuote
        {
            TenantId = loan.TenantId,
            LoanId = loan.Id,
            ClientId = loan.ClientId,
            Reference = reference,
            Issue = issue,
            Status = QuoteStatus.Issued,
            SnapshotJson = json,
            SnapshotHash = ContractSnapshot.HashOf(json),
            TermsHash = snapshot.TermsFingerprint(),
            CreatedUtc = now,
            CreatedByUserId = userId,
            ValidUntilUtc = ValidUntil(now)
        };

        _db.CreditQuotes.Add(quote);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Quote {Reference} issued, valid until {ValidUntil:u}.", reference, quote.ValidUntilUtc);
        return quote;
    }

    // ---------------------------------------------------------------- send

    /// <summary>Creates a fresh link - any earlier one stops working - and sends it.</summary>
    public async Task<ContractService.SendOutcome> SendAsync(
        Guid quoteId, bool bySms, bool byEmail, string publicBaseUrl, string? userId, CancellationToken ct = default)
    {
        var quote = await LoadOpenAsync(quoteId, ct);

        if (!bySms && !byEmail)
            throw new ContractException("Choose SMS, email or both.");

        var snapshot = ReadSnapshot(quote);
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == quote.TenantId, ct);
        var lender = tenant.TradingName ?? tenant.Name;

        var token = NewToken();
        var now = DateTime.UtcNow;
        quote.AccessTokenHash = Hash(token);

        var link = $"{publicBaseUrl.TrimEnd('/')}/q/{token}";
        var until = ContractFormat.SastTime(quote.ValidUntilUtc);
        var sentTo = new List<string>();
        var skipped = new List<string>();
        var context = $"Quote {quote.Reference}";

        if (bySms)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Borrower.Mobile))
                skipped.Add("SMS: no cell number on file");
            else
            {
                await _messages.SendAsync(new OutgoingMessage(
                    MessageChannel.Sms, snapshot.Borrower.Mobile,
                    $"{lender}: your loan quote {quote.Reference} is ready. Read it and accept or decline it here: {link} " +
                    $"It is valid until {until}.",
                    TenantId: quote.TenantId, Context: context,
                    AuditBody: $"{lender}: your loan quote {quote.Reference} is ready: [link]"), ct);
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
                    $"Your loan quote {quote.Reference} from {lender} is ready. It shows exactly what the loan will cost " +
                    "you: the amount, every fee, the interest and each instalment.\n\n" +
                    $"Read it and accept or decline it here:\n{link}\n\n" +
                    $"The quote is binding on {lender} until {until}. Accepting it does not sign anything yet: " +
                    "your credit agreement follows for you to sign.\n\n" +
                    $"If you did not apply for credit with {lender}, do not accept, and contact us on {tenant.ContactNumber}.\n\n{lender}",
                    Subject: $"Your loan quote {quote.Reference}",
                    TenantId: quote.TenantId, Context: context,
                    AuditBody: $"Quote {quote.Reference} link sent (link withheld from the audit copy)."), ct);
                sentTo.Add("email");
            }
        }

        if (sentTo.Count == 0)
            throw new ContractException($"Nothing was sent. {string.Join("; ", skipped)}. Capture the contact details and issue the quote again.");

        quote.Status = quote.Status == QuoteStatus.Viewed ? QuoteStatus.Viewed : QuoteStatus.Sent;
        quote.SentUtc = now;
        quote.SentByUserId = userId;
        quote.SentChannels = string.Join(",", sentTo);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Quote {Reference} sent by {Channels}.", quote.Reference, quote.SentChannels);
        return new ContractService.SendOutcome(link, sentTo, skipped, _messages.IsStubbed);
    }

    // ---------------------------------------------------------------- the client's side

    /// <summary>
    /// The quote a link opens, or null when the link is wrong or withdrawn. An answered or
    /// expired quote still opens, so the client can see what they decided.
    /// </summary>
    public async Task<CreditQuote?> FindByTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;

        var hash = Hash(token);
        var quote = await _db.CreditQuotes.FirstOrDefaultAsync(q => q.AccessTokenHash == hash, ct);
        if (quote is null || quote.Status == QuoteStatus.Voided) return null;

        await ExpireIfDueAsync(quote, ct);
        return quote;
    }

    public async Task RecordViewAsync(CreditQuote quote, CancellationToken ct = default)
    {
        if (quote.FirstViewedUtc is not null) return;

        quote.FirstViewedUtc = DateTime.UtcNow;
        if (quote.Status == QuoteStatus.Sent) quote.Status = QuoteStatus.Viewed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a fresh six-digit PIN to the cell number on the quote. Returns the masked number.</summary>
    public async Task<string> SendPinAsync(CreditQuote quote, CancellationToken ct = default)
    {
        EnsureOpen(quote);

        var mobile = ReadSnapshot(quote).Borrower.Mobile;
        if (string.IsNullOrWhiteSpace(mobile))
            throw new ContractException(
                "There is no cell number on this quote, so it cannot be accepted online. " +
                "Contact the credit provider to accept it in the branch.");

        var now = DateTime.UtcNow;
        if (quote.OtpSentUtc is { } last && now - last < PinResendInterval)
            throw new ContractException("A PIN was sent less than a minute ago. Wait a moment before asking for another.");

        if (quote.OtpWindowStartUtc is null || now - quote.OtpWindowStartUtc >= TimeSpan.FromHours(1))
        {
            quote.OtpWindowStartUtc = now;
            quote.OtpSendsInWindow = 0;
        }

        if (quote.OtpSendsInWindow >= MaxPinsPerHour)
            throw new ContractException("Three PINs have been sent in the last hour. Try again later.");

        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        quote.OtpHash = PinHash(quote.Id, pin);
        quote.OtpExpiresUtc = now + PinLifetime;
        quote.OtpSentUtc = now;
        quote.OtpFailedAttempts = 0;
        quote.OtpSendsInWindow++;
        await _db.SaveChangesAsync(ct);

        await _messages.SendAsync(new OutgoingMessage(
            MessageChannel.Sms, mobile,
            $"Your PIN to accept quote {quote.Reference} is {pin}. It expires in 5 minutes. " +
            "Never share it - the credit provider will not ask you for it.",
            TenantId: quote.TenantId, Context: $"Quote PIN {quote.Reference}",
            AuditBody: $"Your PIN to accept quote {quote.Reference} is ******."), ct);

        return ContractFormat.MaskMobile(mobile);
    }

    /// <summary>Records the client's acceptance: every consent ticked, and the PIN right and in time.</summary>
    public async Task AcceptOnlineAsync(
        CreditQuote quote, string? pin, IReadOnlyCollection<int> ticked, string? ip, string? userAgent, CancellationToken ct = default)
    {
        EnsureOpen(quote);
        var lender = await LenderAsync(quote, ct);
        EnsureAllConsents(ticked, lender);

        if (quote.OtpHash is null || quote.OtpExpiresUtc is null)
            throw new ContractException("Ask for a PIN first.");

        if (quote.OtpExpiresUtc < DateTime.UtcNow)
            throw new ContractException("The PIN has expired. Ask for a new one.");

        var pinOk = !string.IsNullOrWhiteSpace(pin) &&
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(PinHash(quote.Id, pin.Trim())),
                        Encoding.ASCII.GetBytes(quote.OtpHash));

        if (!pinOk)
        {
            quote.OtpFailedAttempts++;
            if (quote.OtpFailedAttempts >= MaxPinAttempts)
            {
                // Burn the PIN: a new one must be requested, which costs a guesser a minute.
                quote.OtpHash = null;
                quote.OtpExpiresUtc = null;
            }

            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Wrong PIN {Attempt} on quote {Reference}.", quote.OtpFailedAttempts, quote.Reference);

            throw new ContractException(quote.OtpFailedAttempts >= MaxPinAttempts
                ? "The PIN is wrong. Too many attempts - ask for a new PIN."
                : "The PIN is wrong.");
        }

        var snapshot = ReadSnapshot(quote);
        quote.Status = QuoteStatus.Accepted;
        quote.AcceptanceMethod = QuoteAcceptanceMethod.OnlineOtp;
        quote.AcceptedUtc = DateTime.UtcNow;
        quote.ConsentsJson = JsonSerializer.Serialize(Consents(lender));
        quote.AcceptedMobile = snapshot.Borrower.Mobile;
        quote.AcceptedIp = Trim(ip, 64);
        quote.AcceptedUserAgent = Trim(userAgent, 400);
        quote.OtpHash = null;
        quote.OtpExpiresUtc = null;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Quote {Reference} accepted online.", quote.Reference);
    }

    public async Task DeclineAsync(CreditQuote quote, string? reason, string? detail, CancellationToken ct = default)
    {
        EnsureOpen(quote);

        if (string.IsNullOrWhiteSpace(reason) || !DeclineReasons.Contains(reason))
            throw new ContractException("Choose why you are declining the quote.");

        if (reason == "Other" && string.IsNullOrWhiteSpace(detail))
            throw new ContractException("Tell us briefly why you are declining the quote.");

        quote.Status = QuoteStatus.Declined;
        quote.DeclinedUtc = DateTime.UtcNow;
        quote.DeclineReason = Trim(string.IsNullOrWhiteSpace(detail) ? reason : $"{reason}: {detail.Trim()}", 500);
        quote.OtpHash = null;
        quote.OtpExpiresUtc = null;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Quote {Reference} declined.", quote.Reference);
    }

    // ---------------------------------------------------------------- staff side

    /// <summary>
    /// Records that the client read and accepted the quote in the branch - for a client
    /// without a cell phone, or who prefers paper. Staff confirm each consent on the client's word.
    /// </summary>
    public async Task RecordInBranchAsync(
        Guid quoteId, IReadOnlyCollection<int> ticked, string? userId, CancellationToken ct = default)
    {
        var quote = await LoadOpenAsync(quoteId, ct);
        var lender = await LenderAsync(quote, ct);
        EnsureAllConsents(ticked, lender);

        quote.Status = QuoteStatus.Accepted;
        quote.AcceptanceMethod = QuoteAcceptanceMethod.InBranch;
        quote.AcceptedUtc = DateTime.UtcNow;
        quote.AcceptedByUserId = userId;
        quote.ConsentsJson = JsonSerializer.Serialize(Consents(lender));
        quote.OtpHash = null;
        quote.OtpExpiresUtc = null;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Quote {Reference} recorded as accepted in the branch.", quote.Reference);
    }

    public async Task VoidAsync(Guid quoteId, string? reason, string? userId, CancellationToken ct = default)
    {
        var quote = await _db.CreditQuotes.FirstOrDefaultAsync(q => q.Id == quoteId, ct)
                    ?? throw new ContractException("The quote was not found.");

        if (quote.Status == QuoteStatus.Voided)
            throw new ContractException("This quote is already voided.");

        if (await _db.LoanContracts.AnyAsync(c => c.LoanId == quote.LoanId && c.Status == ContractStatus.Signed, ct))
            throw new ContractException("The client has signed the agreement made from this quote. It cannot be voided.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ContractException("Record why the quote is being voided.");

        Void(quote, reason.Trim(), userId, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Voids every quote on a loan that is not final - used when the loan itself is cancelled. Does not save.</summary>
    public async Task VoidOpenForLoanAsync(Guid loanId, string reason, string? userId, CancellationToken ct = default)
    {
        var open = await _db.CreditQuotes
            .Where(q => q.LoanId == loanId && q.Status != QuoteStatus.Voided && q.Status != QuoteStatus.Declined)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var q in open) Void(q, reason, userId, now);
    }

    /// <summary>Marks quotes past their validity as expired. Called when a quote is read, so no job is needed.</summary>
    public async Task ExpireIfDueAsync(CreditQuote quote, CancellationToken ct = default)
    {
        if (!quote.IsOpen || quote.ValidUntilUtc > DateTime.UtcNow) return;

        quote.Status = QuoteStatus.Expired;
        quote.OtpHash = null;
        quote.OtpExpiresUtc = null;
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    public static ContractSnapshot ReadSnapshot(CreditQuote quote)
    {
        if (ContractSnapshot.HashOf(quote.SnapshotJson) != quote.SnapshotHash)
            throw new InvalidOperationException(
                $"Quote {quote.Reference} has been altered since it was issued: its fingerprint does not match.");

        return ContractSnapshot.FromJson(quote.SnapshotJson);
    }

    private async Task<CreditQuote> LoadOpenAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await _db.CreditQuotes.FirstOrDefaultAsync(q => q.Id == quoteId, ct)
                    ?? throw new ContractException("The quote was not found.");

        await ExpireIfDueAsync(quote, ct);
        EnsureOpen(quote);
        return quote;
    }

    private static void EnsureOpen(CreditQuote quote)
    {
        if (quote.IsOpen) return;

        throw new ContractException(quote.Status switch
        {
            QuoteStatus.Accepted => "This quote has already been accepted.",
            QuoteStatus.Declined => "This quote was declined.",
            QuoteStatus.Expired => "This quote has expired. Ask the credit provider for a new one.",
            _ => "This quote has been withdrawn."
        });
    }

    private static void EnsureAllConsents(IReadOnlyCollection<int> ticked, string lender)
    {
        var all = Enumerable.Range(0, Consents(lender).Count);
        if (!all.All(ticked.Contains))
            throw new ContractException("Tick all three boxes to accept the quote.");
    }

    private async Task<string> LenderAsync(CreditQuote quote, CancellationToken ct)
    {
        var t = await _db.Tenants.AsNoTracking()
            .Where(x => x.Id == quote.TenantId)
            .Select(x => new { x.TradingName, x.Name })
            .FirstAsync(ct);
        return t.TradingName ?? t.Name;
    }

    private static void Void(CreditQuote q, string reason, string? userId, DateTime now)
    {
        q.Status = QuoteStatus.Voided;
        q.VoidedUtc = now;
        q.VoidedByUserId = userId;
        q.VoidReason = Trim(reason, 500);
        q.AccessTokenHash = null;
        q.OtpHash = null;
        q.OtpExpiresUtc = null;
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string PinHash(Guid quoteId, string pin) => Hash($"quote:{quoteId:N}:{pin}");

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
