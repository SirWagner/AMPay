using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A DebiCheck mandate - the authenticated authority to collect from a debtor account.
/// <para>
/// Two routes reach the same place. TT1 (<see cref="MandateType.DebiCheckTt1RealTime"/>) calls
/// DebiCheckAuthenticate and gets an answer inside the request, so it can land on
/// Authenticated or Rejected synchronously. TT2 goes up in a batch file and the bank answers
/// later through a NetConnector postback, so it sits in SubmittedToBank until then.
/// </para>
/// <para>
/// TT2 has a sequencing constraint TT1 does not: the debit order masterfile entry must already
/// exist at Netcash before a DebiCheckAuthentication instruction is accepted. That is what
/// <see cref="MandateStatus.PendingMasterfile"/> represents.
/// </para>
/// </summary>
public class DebiCheckMandate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public Guid? BankAccountId { get; set; }
    public ClientBankAccount? BankAccount { get; set; }

    public MandateType MandateType { get; set; } = MandateType.DebiCheckTt1RealTime;
    public MandateStatus Status { get; set; } = MandateStatus.Draft;

    /// <summary>Netcash field 101 / AccountReference (AN32). Unique in the tenant masterfile.</summary>
    public string AccountReference { get; set; } = string.Empty;

    /// <summary>
    /// Netcash mandate template id, format NCDCT000000001 (AN14). Configured on the Netcash
    /// side; a real-time template is required for TT1 or the call returns error 325.
    /// </summary>
    public string? MandateTemplateId { get; set; }

    /// <summary>Returned by Netcash on successful authentication. The handle for amend and cancel.</summary>
    public string? ContractReference { get; set; }

    /// <summary>Netcash field 249 (AN50). Quoted on every collection against this mandate.</summary>
    public string? MandateReference { get; set; }

    // ---- Collection instruction ----

    /// <summary>Stored in rands. Converted to cents at the Netcash boundary, never before.</summary>
    public decimal CollectionAmount { get; set; }

    public bool FirstCollectionDiffers { get; set; }
    public decimal? FirstCollectionAmount { get; set; }
    public DateTime? FirstCollectionDate { get; set; }

    public DebitFrequency Frequency { get; set; } = DebitFrequency.Monthly;
    public string? CollectionDayCode { get; set; }

    /// <summary>Field 232. Netcash accepts 1 to 10 days.</summary>
    public int TrackingDays { get; set; } = 5;

    // ---- Netcash response trail ----

    /// <summary>Netcash error code. "000" on success.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Bank response code. "900000" on success.</summary>
    public string? BankResponseCode { get; set; }

    /// <summary>Bankserv response code. "ACCP" on success.</summary>
    public string? BankservResponseCode { get; set; }

    public string? ClientResponseCode { get; set; }
    public string? ResponseMessage { get; set; }

    /// <summary>Registered Mandate Service flag from the postback. Not available for TT1.</summary>
    public bool? RmsApplied { get; set; }

    /// <summary>File token from BatchFileUpload on the TT2 route.</summary>
    public string? FileToken { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? AuthenticatedUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    public ICollection<MandateEvent> Events { get; set; } = new List<MandateEvent>();

    public bool IsCollectable => Status == MandateStatus.Authenticated;
}

/// <summary>
/// Append-only audit of everything that happened to a mandate, including every raw
/// Netcash request and response. Payment disputes are won or lost on this trail.
/// </summary>
public class MandateEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MandateId { get; set; }
    public DebiCheckMandate? Mandate { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Submitted, PostbackReceived, Authenticated, Rejected, Cancelled, Amended.</summary>
    public string EventType { get; set; } = string.Empty;

    public MandateStatus? FromStatus { get; set; }
    public MandateStatus? ToStatus { get; set; }

    public string? Detail { get; set; }

    /// <summary>Raw payload, scrubbed of account numbers before persisting.</summary>
    public string? RawPayload { get; set; }

    public string? UserId { get; set; }
}
