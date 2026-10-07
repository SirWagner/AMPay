using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// The legal wording of one section of a lender's contract pack.
/// <para>
/// Each lender trading under the AM-Pay ISV has its own NCR registration and its own
/// attorney-approved terms, so templates are per tenant. A template stays a draft until an
/// administrator records that it has been approved; documents produced from a draft are
/// watermarked as such.
/// </para>
/// </summary>
public class ContractTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public ContractTemplateKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Plain text. Blank lines separate paragraphs; a paragraph starting with a number and a
    /// full stop ("3.") is rendered as a numbered clause.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Set when someone with authority confirms the wording is legally approved.</summary>
    public bool IsApproved { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public string? ApprovedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
}

/// <summary>
/// The contract pack for one loan: credit agreement, debit order authorisation, budget
/// acknowledgement and credit life disclosure, frozen at issue.
/// <para>
/// <see cref="SnapshotJson"/> holds every word and figure the client sees. The HTML view
/// and the PDF are both rendered from it, and <see cref="SnapshotHash"/> fingerprints it,
/// so what was signed can be proved and can never drift from what was shown.
/// </para>
/// </summary>
public class LoanContract
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid LoanId { get; set; }
    public Loan? Loan { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Loan number plus issue: "LN-000003/1", "/2" after a re-issue.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>1 for the first issue; incremented each time a voided pack is replaced.</summary>
    public int Issue { get; set; } = 1;

    public ContractStatus Status { get; set; } = ContractStatus.Issued;

    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="SnapshotJson"/>, hex.</summary>
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>False when any section came from a template not yet legally approved.</summary>
    public bool TemplatesApproved { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    // ---- Sending ----

    public DateTime? SentUtc { get; set; }
    public string? SentByUserId { get; set; }

    /// <summary>"Sms", "Email" or "Sms,Email".</summary>
    public string? SentChannels { get; set; }

    /// <summary>
    /// SHA-256 of the access token in the client's link. The token itself is never stored:
    /// a copy of the database is not a set of working signing links.
    /// </summary>
    public string? AccessTokenHash { get; set; }
    public DateTime? AccessExpiresUtc { get; set; }

    public DateTime? FirstViewedUtc { get; set; }

    // ---- One-time code ----

    public string? OtpHash { get; set; }
    public DateTime? OtpExpiresUtc { get; set; }
    public DateTime? OtpSentUtc { get; set; }
    public int OtpFailedAttempts { get; set; }

    // ---- Signature ----

    public SignatureMethod SignatureMethod { get; set; } = SignatureMethod.None;
    public DateTime? SignedUtc { get; set; }

    /// <summary>The name the client typed when signing.</summary>
    public string? SignedName { get; set; }

    /// <summary>Copied from the client record at signature, as Maxmoney's stamp does.</summary>
    public string? SignedIdNumber { get; set; }
    public string? SignedMobile { get; set; }
    public string? SignedIp { get; set; }
    public string? SignedUserAgent { get; set; }

    /// <summary>Staff member who uploaded a paper-signed copy.</summary>
    public string? SignedRecordedByUserId { get; set; }

    /// <summary>The uploaded paper-signed scan, when signed that way.</summary>
    public Guid? SignedCopyDocumentId { get; set; }
    public ClientDocument? SignedCopyDocument { get; set; }

    // ---- Withdrawal ----

    public DateTime? VoidedUtc { get; set; }
    public string? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }

    public bool IsOpen => Status is ContractStatus.Issued or ContractStatus.Sent or ContractStatus.Viewed;
}

/// <summary>
/// Every SMS and email the platform sends, or would send. With no provider configured the
/// message is recorded here only, which is how one-time codes are read during testing.
/// </summary>
public class OutboundMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }

    public MessageChannel Channel { get; set; }
    public string To { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;

    public MessageStatus Status { get; set; }
    public string? ProviderReference { get; set; }
    public string? Error { get; set; }

    /// <summary>What the message was about, e.g. "contract:{id}". For tracing, not logic.</summary>
    public string? Context { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
