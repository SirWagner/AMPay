using AMPay.Domain.Enums;

namespace AMPay.Domain.Messaging;

/// <param name="AuditBody">
/// What the outbox keeps once messages are really delivered, when the body holds a secret
/// such as a signing PIN. Staff can read the outbox; they must not be able to sign for a client.
/// The stub keeps the full body, because in testing the outbox is the only place to read the PIN.
/// </param>
public record OutgoingMessage(
    MessageChannel Channel,
    string To,
    string Body,
    string? Subject = null,
    Guid? TenantId = null,
    string? Context = null,
    string? AuditBody = null);

public record SendResult(bool Delivered, MessageStatus Status, string? Error = null);

/// <summary>
/// Sends an SMS or an email to a client.
/// <para>
/// Same shape as the INetcash* contracts: the application talks to this interface, and a
/// provider (Clickatell, BulkSMS, SendGrid, Microsoft 365) is a registration change. Until
/// one is chosen, the outbox implementation records each message instead of sending it.
/// </para>
/// </summary>
public interface IMessageSender
{
    /// <summary>True when messages are only recorded, never delivered.</summary>
    bool IsStubbed { get; }

    Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken ct = default);
}

public class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Record messages in the outbox instead of sending them.</summary>
    public bool UseStubs { get; set; } = true;

    /// <summary>
    /// The address clients reach the public pages on - signing links point here. Empty
    /// means "the address of the request that created the link", fine for local testing.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
