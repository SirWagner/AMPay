using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// One NIF file submitted to Netcash via BatchFileUpload, and the load report that came back.
/// <para>
/// Netcash batch submission is two-phase: upload returns a file token immediately, then the
/// load report is fetched separately with RequestFileUploadReport. The report is where
/// per-line validation errors appear, so a token alone never means the batch succeeded.
/// </para>
/// </summary>
public class NetcashBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Header field 5. Our own identifier, echoed back in the load report.</summary>
    public string BatchName { get; set; } = string.Empty;

    /// <summary>Header field 4: Update, Sameday, TwoDay, DebiCheckAuthentication.</summary>
    public string Instruction { get; set; } = string.Empty;

    public NetcashServiceId ServiceId { get; set; } = NetcashServiceId.DebitOrders;

    /// <summary>Header field 6, held as a date and formatted CCYYMMDD at the boundary.</summary>
    public DateTime ActionDate { get; set; }

    public int TransactionCount { get; set; }

    /// <summary>Footer field 3, in cents, matching what was actually sent.</summary>
    public long TotalAmountCents { get; set; }

    public BatchStatus Status { get; set; } = BatchStatus.Draft;

    /// <summary>Returned by BatchFileUpload.</summary>
    public string? FileToken { get; set; }

    /// <summary>Raw tab-delimited load report, retained verbatim for audit.</summary>
    public string? LoadReport { get; set; }

    /// <summary>SUCCESSFUL, SUCCESSFUL WITH ERRORS, or UNSUCCESSFUL.</summary>
    public string? LoadReportResult { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UploadedUtc { get; set; }
    public DateTime? ReportRetrievedUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    public ICollection<NetcashBatchError> Errors { get; set; } = new List<NetcashBatchError>();
}

/// <summary>A single line-level error parsed out of a load report message section.</summary>
public class NetcashBatchError
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public NetcashBatch? Batch { get; set; }

    public string? UniqueReference { get; set; }
    public int? LineNumber { get; set; }
    public string Message { get; set; } = string.Empty;
}
