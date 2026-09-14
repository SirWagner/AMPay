using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A Pay Now checkout transaction. The customer is redirected to the Netcash hosted payment
/// page, so no card data ever reaches this application - which is what keeps the platform
/// inside PCI DSS SAQ A rather than the far heavier SAQ D.
/// </summary>
public class PayNowTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Optional - a checkout need not belong to a captured client.</summary>
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Netcash m_payment_reference. Our unique handle for this attempt.</summary>
    public string PaymentReference { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ZAR";

    public string? Description { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerMobile { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Created;

    /// <summary>Netcash transaction id, returned on the accept or notify callback.</summary>
    public string? NetcashTransactionId { get; set; }

    /// <summary>Method the customer chose: Card, InstantEFT, PayShap, QR, Masterpass.</summary>
    public string? PaymentMethod { get; set; }

    public string? ResponseCode { get; set; }
    public string? ResponseMessage { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RedirectedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Raw notify payload for reconciliation against the Netcash statement.</summary>
    public string? RawNotifyPayload { get; set; }
}
