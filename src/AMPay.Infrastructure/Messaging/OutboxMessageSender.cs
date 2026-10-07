using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Messaging;

/// <summary>
/// Records every message in the outbox and delivers none.
/// <para>
/// GAP - SMS and email providers. Implement <see cref="IMessageSender"/> against the chosen
/// provider and register it when Messaging:UseStubs is false. Keep writing the outbox row
/// in the real implementation too: it is the audit trail of what each client was sent -
/// storing <see cref="OutgoingMessage.AuditBody"/> in place of the body when it is set.
/// </para>
/// </summary>
public class OutboxMessageSender : IMessageSender
{
    private readonly AppDbContext _db;
    private readonly ILogger<OutboxMessageSender> _log;

    public OutboxMessageSender(AppDbContext db, ILogger<OutboxMessageSender> log)
    {
        _db = db;
        _log = log;
    }

    public bool IsStubbed => true;

    public async Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        _db.OutboundMessages.Add(new OutboundMessage
        {
            TenantId = message.TenantId,
            Channel = message.Channel,
            To = message.To,
            Subject = message.Subject,
            Body = message.Body,
            Context = message.Context,
            Status = MessageStatus.Stubbed
        });

        await _db.SaveChangesAsync(ct);

        // Warning, not Information: a stubbed message must never be mistaken for a sent one.
        _log.LogWarning("Stubbed {Channel} to {To} ({Context}) - recorded in the outbox, not delivered.",
            message.Channel, Mask(message.To), message.Context);

        return new SendResult(Delivered: false, MessageStatus.Stubbed);
    }

    /// <summary>Logs carry enough to trace a message, not a full cell number or address.</summary>
    private static string Mask(string to) =>
        to.Length <= 4 ? "****" : new string('*', to.Length - 4) + to[^4..];
}
