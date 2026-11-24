namespace AMPay.Domain.Entities;

/// <summary>
/// One thing that happened in a lender's business - a payment, an unpaid debit, a signed
/// agreement, a new client. Written as the change is saved, so the feed is the business's
/// activity as it happened, not a report run later.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Payment, Unpaid, Loan, Agreement, DebitOrder, Client, PayNow.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Good news, needs attention, or neither.</summary>
    public NotificationTone Tone { get; set; }

    public string Title { get; set; } = "";
    public string? Body { get; set; }

    /// <summary>Where to go to act on it, e.g. /Loans/Details/{id}.</summary>
    public string? Link { get; set; }

    public Guid? ClientId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum NotificationTone { Info = 0, Good = 1, Attention = 2 }

/// <summary>When a user last opened the notifications for a lender: everything newer is unread.</summary>
public class NotificationSeen
{
    public string UserId { get; set; } = "";
    public Guid TenantId { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
