using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AMPay.Web.Services;

/// <summary>
/// Writes the lender's notification feed as things happen: on every save it looks at what
/// changed - payments, unpaid debits, loans, agreements, debit orders, new clients, Pay Now -
/// and adds one notification per event to the same save. Nothing is polled or recomputed.
/// </summary>
public class ActivityRecorder : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public static void Record(DbContext? context)
    {
        if (context is not AppDbContext db) return;
        db.ChangeTracker.DetectChanges();

        var found = new List<Notification>();
        foreach (var e in db.ChangeTracker.Entries().ToList())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified)) continue;
            var n = e.Entity switch
            {
                LoanCollection c => Collection(db, e, c),
                Loan l => LoanEvent(db, e, l),
                LoanContract k => Contract(db, e, k),
                DebiCheckMandate m => Mandate(db, e, m),
                PayNowTransaction p => PayNow(e, p),
                Client c when e.State == EntityState.Added => new Notification
                {
                    TenantId = c.TenantId, Kind = "Client", Tone = NotificationTone.Info, ClientId = c.Id,
                    Title = $"New client captured: {c.FirstName} {c.Surname}".Trim(), Link = $"/Clients/Details/{c.Id}"
                },
                _ => null
            };
            if (n is not null && n.TenantId != Guid.Empty) found.Add(n);
        }

        if (found.Count > 0) db.Notifications.AddRange(found);
    }

    // ---------------------------------------------------------------- events

    private static Notification? Collection(AppDbContext db, EntityEntry e, LoanCollection c)
    {
        if (!Became(e, nameof(LoanCollection.Status), out _)) return null;
        var loan = LocalLoan(db, c.LoanId);
        var who = ClientName(db, loan?.ClientId);
        var number = loan?.LoanNumber ?? "a loan";
        var how = c.Channel switch { CollectionChannel.DebiCheck => "debit order", CollectionChannel.PayNow => "Pay Now", _ => "payment" };

        return c.Status switch
        {
            CollectionStatus.Successful => Note(c.TenantId, "Payment", NotificationTone.Good,
                $"R {c.Amount:N2} received on {number}{who}", $"By {how}{(c.ExternalReference is null ? "" : $", ref {c.ExternalReference}")}.",
                $"/Loans/Details/{c.LoanId}", loan?.ClientId),
            CollectionStatus.Failed => Note(c.TenantId, "Unpaid", NotificationTone.Attention,
                $"Debit of R {c.Amount:N2} returned unpaid on {number}{who}",
                UnpaidCodes.Label(c.FailureGroup ?? UnpaidCodes.GroupOf(c.FailureCode)) + (c.FailureCode is null ? "" : $" (code {c.FailureCode})") + ". Contact the client.",
                $"/Loans/Details/{c.LoanId}", loan?.ClientId),
            CollectionStatus.Reversed => Note(c.TenantId, "Payment", NotificationTone.Attention,
                $"Payment of R {c.Amount:N2} reversed on {number}{who}", null, $"/Loans/Details/{c.LoanId}", loan?.ClientId),
            _ => null
        };
    }

    private static Notification? LoanEvent(AppDbContext db, EntityEntry e, Loan l)
    {
        var who = ClientName(db, l.ClientId);
        if (e.State == EntityState.Added && l.Status != LoanStatus.Draft || Became(e, nameof(Loan.Status), out _))
        {
            var (title, tone) = l.Status switch
            {
                LoanStatus.PendingApproval => ($"Loan {l.LoanNumber}{who} waiting for approval - R {l.Principal:N2}", NotificationTone.Attention),
                LoanStatus.Approved => ($"Loan {l.LoanNumber}{who} approved - R {l.Principal:N2}", NotificationTone.Good),
                LoanStatus.Declined => ($"Loan {l.LoanNumber}{who} declined", NotificationTone.Info),
                LoanStatus.Disbursed => ($"Loan {l.LoanNumber}{who} paid out - R {l.Principal:N2}", NotificationTone.Good),
                LoanStatus.Settled => ($"Loan {l.LoanNumber}{who} paid in full", NotificationTone.Good),
                LoanStatus.Cancelled => ($"Loan {l.LoanNumber}{who} cancelled", NotificationTone.Info),
                LoanStatus.WrittenOff => ($"Loan {l.LoanNumber}{who} written off - R {l.WrittenOffAmount ?? 0m:N2}", NotificationTone.Attention),
                _ => ((string?)null, NotificationTone.Info)
            };
            if (title is null) return null;
            return Note(l.TenantId, "Loan", tone, title, null, $"/Loans/Details/{l.Id}", l.ClientId);
        }
        return null;
    }

    private static Notification? Contract(AppDbContext db, EntityEntry e, LoanContract k)
    {
        if (!Became(e, nameof(LoanContract.Status), out _)) return null;
        var who = ClientName(db, k.ClientId);
        return k.Status switch
        {
            ContractStatus.Signed => Note(k.TenantId, "Agreement", NotificationTone.Good,
                $"Agreement {k.Reference}{who} signed", null, $"/Loans/Details/{k.LoanId}", k.ClientId),
            ContractStatus.Viewed => Note(k.TenantId, "Agreement", NotificationTone.Info,
                $"Agreement {k.Reference}{who} opened by the client", null, $"/Loans/Details/{k.LoanId}", k.ClientId),
            _ => null
        };
    }

    private static Notification? Mandate(AppDbContext db, EntityEntry e, DebiCheckMandate m)
    {
        if (!Became(e, nameof(DebiCheckMandate.Status), out _)) return null;
        var who = ClientName(db, m.ClientId);
        var (title, tone) = m.Status switch
        {
            MandateStatus.Authenticated => ($"Debit order approved by the client{who} - R {m.CollectionAmount:N2}", NotificationTone.Good),
            MandateStatus.AwaitingDebtorAuthentication => ($"Debit order sent to the client's bank for approval{who}", NotificationTone.Info),
            MandateStatus.Rejected => ($"Debit order rejected at the bank{who}", NotificationTone.Attention),
            MandateStatus.Cancelled => ($"Debit order cancelled{who}", NotificationTone.Attention),
            MandateStatus.Expired => ($"Debit order approval expired{who}", NotificationTone.Attention),
            MandateStatus.Failed => ($"Debit order could not be registered{who}", NotificationTone.Attention),
            _ => ((string?)null, NotificationTone.Info)
        };
        return title is null ? null : Note(m.TenantId, "DebitOrder", tone, title, m.ResponseMessage, $"/Mandates/Details/{m.Id}", m.ClientId);
    }

    private static Notification? PayNow(EntityEntry e, PayNowTransaction p)
    {
        if (!Became(e, nameof(PayNowTransaction.Status), out _)) return null;
        return p.Status switch
        {
            PaymentStatus.Complete => Note(p.TenantId, "PayNow", NotificationTone.Good,
                $"Pay Now payment of R {p.Amount:N2} completed{(p.CustomerName is null ? "" : " by " + p.CustomerName)}", p.PaymentReference, "/Checkout", p.ClientId),
            PaymentStatus.Declined => Note(p.TenantId, "PayNow", NotificationTone.Attention,
                $"Pay Now payment of R {p.Amount:N2} declined{(p.CustomerName is null ? "" : " for " + p.CustomerName)}", p.ResponseMessage, "/Checkout", p.ClientId),
            _ => null
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>True when the property was set on an insert, or changed on an update.</summary>
    private static bool Became(EntityEntry e, string property, out object? value)
    {
        var p = e.Property(property);
        value = p.CurrentValue;
        return e.State == EntityState.Added || (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue));
    }

    private static Loan? LocalLoan(AppDbContext db, Guid loanId) =>
        db.Loans.Local.FirstOrDefault(l => l.Id == loanId) ?? db.Loans.AsNoTracking().FirstOrDefault(l => l.Id == loanId);

    private static string ClientName(AppDbContext db, Guid? clientId)
    {
        if (clientId is null) return "";
        var c = db.Clients.Local.FirstOrDefault(x => x.Id == clientId)
                ?? db.Clients.AsNoTracking().Where(x => x.Id == clientId).Select(x => new Client { FirstName = x.FirstName, Surname = x.Surname }).FirstOrDefault();
        return c is null ? "" : $" ({c.FirstName} {c.Surname})";
    }

    private static Notification Note(Guid tenantId, string kind, NotificationTone tone, string title, string? body, string? link, Guid? clientId) => new()
    {
        TenantId = tenantId, Kind = kind, Tone = tone,
        Title = title.Length > 300 ? title[..300] : title,
        Body = body is { Length: > 1000 } ? body[..1000] : body,
        Link = link, ClientId = clientId
    };
}

/// <summary>How many notifications the signed-in user has not seen yet, for the header.</summary>
public class NotificationCounter
{
    private readonly AppDbContext _db;
    public NotificationCounter(AppDbContext db) => _db = db;

    public async Task<int> UnreadAsync(string? userId, Guid? tenantId)
    {
        if (userId is null || tenantId is null) return 0;
        var seen = await _db.NotificationsSeen.AsNoTracking()
            .Where(s => s.UserId == userId && s.TenantId == tenantId).Select(s => (DateTime?)s.LastSeenUtc).FirstOrDefaultAsync();
        var since = seen ?? DateTime.UtcNow.AddDays(-7);
        return await _db.Notifications.CountAsync(n => n.TenantId == tenantId && n.CreatedUtc > since);
    }
}
