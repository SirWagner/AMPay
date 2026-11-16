using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>Address tab. A client may hold physical, postal and work addresses.</summary>
public class ClientAddress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public AddressType AddressType { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; } = "South Africa";

    // Contact details live with the address tab, as they do in Maxmoney.
    public string? HomeTelephone { get; set; }
    /// <summary>Netcash field 202 (N11). Required for DebiCheck - the debtor is contacted here.</summary>
    public string? MobileNumber { get; set; }
    public string? EmailAddress { get; set; }
}

/// <summary>References tab.</summary>
public class ClientReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public string? MobileNumber { get; set; }
    public string? Telephone { get; set; }
    public string? EmailAddress { get; set; }

    public string? AddressLine1 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Comments { get; set; }
}

/// <summary>
/// One line of the client's monthly budget, captured on the Financial step. The expense and
/// debt totals on <see cref="ClientFinancial"/> are the sums of these lines.
/// </summary>
public class ClientBudget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public BudgetLineKind Kind { get; set; } = BudgetLineKind.Expense;

    /// <summary>
    /// A <see cref="Credit.BudgetCategories"/> key, or <see cref="Credit.BudgetCategories.Custom"/>
    /// for a line the operator added.
    /// </summary>
    public string? Category { get; set; }

    public int DisplayOrder { get; set; }

    public string Description { get; set; } = string.Empty;
    public DateTime BudgetDate { get; set; } = DateTime.UtcNow.Date;
    public string? BudgetType { get; set; }
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Credit Check tab (Maxmoney calls this Compuscan): one bureau enquiry through Netcash risk
/// reports, from request to the report PDF.
/// </summary>
public class ClientCreditEnquiry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public DateTime EnquiryDate { get; set; } = DateTime.UtcNow;
    public string? Bureau { get; set; }

    /// <summary>Netcash's file token for collecting the report.</summary>
    public string? EnquiryReference { get; set; }

    public int? Score { get; set; }

    /// <summary>What happened, in words: waiting for the bureau, report received, or why it failed.</summary>
    public string? Outcome { get; set; }

    public CreditEnquiryStatus Status { get; set; } = CreditEnquiryStatus.Submitted;

    /// <summary>The bureau reason code sent (32 = credit application).</summary>
    public int? ReasonCode { get; set; }

    /// <summary>When the operator recorded that the client consented to this enquiry.</summary>
    public DateTime? ConsentRecordedUtc { get; set; }

    public string? RequestedByUserId { get; set; }

    /// <summary>Where the report PDF is stored (private document store), once received.</summary>
    public string? ReportPointer { get; set; }

    public DateTime? ReportReceivedUtc { get; set; }
}

public enum CreditEnquiryStatus
{
    /// <summary>Sent to Netcash; the bureau has not answered yet.</summary>
    Submitted = 0,
    ReportReceived = 1,
    Failed = 2
}

/// <summary>
/// Notes tab - the client's contact history: every call, message and visit, and when to get
/// back to them. The lender's CRM, one client at a time; open follow-ups are listed together.
/// </summary>
public class ClientNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>How the contact happened: one of <see cref="ContactChannels.All"/>.</summary>
    public string? Category { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    /// <summary>When to get back to the client. Null for a note that needs nothing more.</summary>
    public DateTime? FollowUpOn { get; set; }
    public DateTime? FollowUpDoneUtc { get; set; }
    public string? FollowUpDoneBy { get; set; }

    public bool FollowUpOpen => FollowUpOn is not null && FollowUpDoneUtc is null;
}

public static class ContactChannels
{
    public static readonly string[] All = { "Phone call", "WhatsApp", "SMS", "Email", "Branch visit", "Note" };
}

/// <summary>
/// Documents tab. Files are not stored in the database - <see cref="StoragePath"/> points at
/// Azure Blob Storage (private container, server-side encryption).
/// </summary>
public class ClientDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Blob path. Never a public URL - serve through an authorised controller action.</summary>
    public string StoragePath { get; set; } = string.Empty;

    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
    public string? UploadedByUserId { get; set; }

    // ---- Review ----
    // A document is uploaded by a capturer and passed by a reviewer. The two are separate
    // people on purpose: the person who onboards a client should not be the only person who
    // attests that the client's ID is genuine.

    public DocumentReviewStatus ReviewStatus { get; set; } = DocumentReviewStatus.Pending;

    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewedByUserId { get; set; }

    /// <summary>Why a document was rejected. Required on rejection so the capturer can fix it.</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>SHA-256 of the stored bytes. Detects a file swapped after it was approved.</summary>
    public string? ContentHash { get; set; }
}

/// <summary>Take Picture tab - the client's photograph, captured from webcam or uploaded.</summary>
public class ClientPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string StoragePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string? CapturedByUserId { get; set; }
}
