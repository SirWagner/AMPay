using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Messaging;
using AMPay.Domain.Portal;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AMPay.Web.Services;

/// <summary>
/// Keeps a client's own account on the self-service portal in step with AM-Pay: the lender
/// gives the client access, AM-Pay sends the full statement, and sends it again whenever a
/// loan, a payment or the debit order changes. The portal never asks - AM-Pay always tells.
/// </summary>
public class ClientPortalPublisher
{
    private readonly AppDbContext _db;
    private readonly IPortalApi _portal;
    private readonly IMessageSender _messages;
    private readonly ILogger<ClientPortalPublisher> _log;
    private readonly AMPay.Infrastructure.Contracts.ContractService? _contracts;

    public ClientPortalPublisher(AppDbContext db, IPortalApi portal, IMessageSender messages, ILogger<ClientPortalPublisher> log,
        AMPay.Infrastructure.Contracts.ContractService? contracts = null)
    {
        _db = db;
        _portal = portal;
        _messages = messages;
        _log = log;
        _contracts = contracts;
    }

    private Task<List<LoanContract>> SignedContractsAsync(Guid clientId, CancellationToken ct) =>
        _db.LoanContracts.AsNoTracking().Include(c => c.Loan)
            .Where(c => c.ClientId == clientId && c.Status == ContractStatus.Signed)
            .ToListAsync(ct);

    public bool IsConfigured => _portal.IsConfigured;

    /// <summary>The address a client signs in at to see their loans, or null without a self-service link.</summary>
    public string? AccountLink(Tenant tenant) =>
        _portal.LinkFor(tenant.SelfServiceCode) is { } link ? link + "/account" : null;

    /// <summary>The statement the client would see now. Also what the lender previews.</summary>
    public async Task<PortalClientStatement> BuildAsync(Client client, CancellationToken ct = default)
    {
        if (!_db.Entry(client).Collection(c => c.Addresses).IsLoaded)
            await _db.Entry(client).Collection(c => c.Addresses).LoadAsync(ct);

        var loans = await _db.Loans.AsNoTracking()
            .Where(l => l.TenantId == client.TenantId && l.ClientId == client.Id && l.Status != LoanStatus.Draft)
            .Include(l => l.Schedule)
            .Include(l => l.Collections)
            .Include(l => l.CreditPackage)
            .Include(l => l.Mandate).ThenInclude(m => m!.BankAccount)
            .AsSplitQuery()
            .ToListAsync(ct);

        var documents = (await SignedContractsAsync(client.Id, ct))
            .Select(c => new PortalDocumentInfo(ClientStatementBuilder.AgreementKey(c.Id), c.Loan?.LoanNumber,
                $"Signed credit agreement {c.Reference}", c.SignedUtc ?? c.CreatedUtc));

        var now = DateTime.UtcNow;
        var pending = new List<PortalPendingAction>();
        pending.AddRange((await _db.CreditQuotes.AsNoTracking().Include(q => q.Loan)
                .Where(q => q.ClientId == client.Id && q.ClientAppLink != null &&
                            (q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed) && q.ValidUntilUtc > now)
                .ToListAsync(ct))
            .Select(q => new PortalPendingAction("Quote", q.Reference, q.Loan?.LoanNumber,
                "Your loan quote is ready - read it and accept or decline", q.ClientAppLink!, q.ValidUntilUtc)));
        pending.AddRange((await _db.LoanContracts.AsNoTracking().Include(c => c.Loan)
                .Where(c => c.ClientId == client.Id && c.ClientAppLink != null &&
                            (c.Status == ContractStatus.Sent || c.Status == ContractStatus.Viewed) &&
                            (c.AccessExpiresUtc == null || c.AccessExpiresUtc > now))
                .ToListAsync(ct))
            .Select(c => new PortalPendingAction("Agreement", c.Reference, c.Loan?.LoanNumber,
                "Your credit agreement is ready to read and sign", c.ClientAppLink!, c.AccessExpiresUtc)));

        pending.AddRange(await _db.Mandates.AsNoTracking()
            .Where(m => m.ClientId == client.Id && m.MandateType == MandateType.NetcashEMandate &&
                        m.Status == MandateStatus.AwaitingDebtorAuthentication && m.SigningUrl != null)
            .Select(m => new PortalPendingAction("DebitOrder", m.AccountReference, null,
                "Sign your debit order mandate", m.SigningUrl!, null))
            .ToListAsync(ct));

        return ClientStatementBuilder.Build(client, loans, Sast.DateOf(now), now, documents, pending);
    }

    /// <summary>
    /// Gives the client access and sends them the link by SMS. Returns an error for the person
    /// on screen, or null.
    /// </summary>
    public async Task<string?> GrantAccessAsync(Client client, string? userId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstAsync(t => t.Id == client.TenantId, ct);
        if (!_portal.IsConfigured) return "The self-service portal is not set up for AM-Pay yet.";
        if (AccountLink(tenant) is not { } link)
            return "Create your self-service link first (Settings → Self-service link). Clients sign in through it.";

        await _db.Entry(client).Collection(c => c.Addresses).LoadAsync(ct);
        var mobile = ClientStatementBuilder.MobileOf(client);
        if (mobile is null) return "Capture the client's cell number first - they sign in with a code sent to it.";

        client.PortalAccessUtc ??= DateTime.UtcNow;
        var error = await PublishAsync(client, force: true, ct);
        if (error is not null)
        {
            client.PortalAccessUtc = null;
            await _db.SaveChangesAsync(ct);
            return error;
        }

        var name = string.IsNullOrWhiteSpace(tenant.TradingName) ? tenant.Name : tenant.TradingName;
        await _messages.SendAsync(new OutgoingMessage(
            MessageChannel.Sms, PortalApi.DisplayMobile(mobile),
            $"{name}: see your loans, payments and statement online at {link} - sign in with a code sent to this number.",
            TenantId: tenant.Id, Context: $"portal-access:{client.Id}"), ct);

        _db.ClientNotes.Add(new ClientNote
        {
            ClientId = client.Id,
            Category = "Client portal",
            Body = $"Online account opened for {PortalApi.DisplayMobile(mobile)}; link sent by SMS.",
            CreatedByUserId = userId
        });
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Withdraws access: the portal deletes the account view and unlinks whoever held it.</summary>
    public async Task<string?> RevokeAccessAsync(Client client, string? userId, CancellationToken ct = default)
    {
        if (client.PortalAccessUtc is null) return null;
        try
        {
            if (_portal.IsConfigured) await _portal.DeleteClientAsync(client.TenantId, client.Id, ct);
        }
        catch (PortalUnavailableException ex)
        {
            return ex.Message;
        }

        client.PortalAccessUtc = null;
        client.PortalPublishedUtc = null;
        client.PortalStatementHash = null;
        client.PortalDocumentKeys = null;
        _db.ClientNotes.Add(new ClientNote
        {
            ClientId = client.Id, Category = "Client portal", Body = "Online account access withdrawn.", CreatedByUserId = userId
        });
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// After a quote or agreement is sent to the client's app: opens the client's online account
    /// if it is not open yet (the sign-in link goes by SMS), else updates it at once. Returns a note for staff.
    /// </summary>
    public async Task<string> ShowInAppAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.Include(c => c.Addresses).FirstAsync(c => c.Id == clientId, ct);
        var error = client.PortalAccessUtc is null
            ? await GrantAccessAsync(client, null, ct)
            : await PublishAsync(client, force: true, ct);
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == client.TenantId, ct);
        return error is null
            ? $"It is waiting in the client's app: they sign in at {AccountLink(tenant)} with a code to their cell."
            : "Not shown in the client's app: " + error;
    }

    /// <summary>
    /// Sends the client's statement when it has changed since the last one (or always, with
    /// <paramref name="force"/>). Does nothing for a client without access. Returns an error, or null.
    /// </summary>
    public async Task<string?> PublishAsync(Client client, bool force = false, CancellationToken ct = default)
    {
        if (client.PortalAccessUtc is null || !_portal.IsConfigured) return null;

        var statement = await BuildAsync(client, ct);
        if (string.IsNullOrEmpty(statement.Mobile))
            return "The client has no cell number, so they cannot sign in. Capture one, then update the portal.";

        var hash = Fingerprint(statement);
        if (!force && hash == client.PortalStatementHash) return null;

        try
        {
            await _portal.PutClientAsync(statement, ct);
        }
        catch (PortalUnavailableException ex)
        {
            _log.LogWarning("Client {ClientId} not sent to the portal: {Message}", client.Id, ex.Message);
            return ex.Message;
        }

        client.PortalStatementHash = hash;
        client.PortalPublishedUtc = statement.GeneratedUtc;
        await SendDocumentsAsync(client, force, ct);
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Sends each signed agreement the portal does not hold yet, as the client's copy of the PDF.</summary>
    private async Task SendDocumentsAsync(Client client, bool force, CancellationToken ct)
    {
        if (_contracts is null) return;
        var sent = new HashSet<string>((client.PortalDocumentKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));

        foreach (var contract in await SignedContractsAsync(client.Id, ct))
        {
            var key = ClientStatementBuilder.AgreementKey(contract.Id);
            if (!force && sent.Contains(key)) continue;
            try
            {
                var pdf = _contracts.RenderPublicPdf(contract);
                await _portal.PutClientDocumentAsync(client.TenantId, client.Id, key, new PortalDocumentUpload(
                    $"Signed credit agreement {contract.Reference}", contract.Loan?.LoanNumber, "application/pdf",
                    Convert.ToBase64String(pdf), contract.SignedUtc ?? contract.CreatedUtc), ct);
                sent.Add(key);
            }
            catch (Exception ex) when (ex is PortalUnavailableException or InvalidOperationException)
            {
                _log.LogWarning("Agreement {Reference} not sent to the portal: {Message}", contract.Reference, ex.Message);
            }
        }
        client.PortalDocumentKeys = sent.Count == 0 ? null : string.Join(',', sent);
    }

    /// <summary>Publishes the given clients, those with access only. Used after loans or payments change.</summary>
    public async Task PublishClientsAsync(IReadOnlyCollection<Guid> clientIds, CancellationToken ct = default)
    {
        if (clientIds.Count == 0 || !_portal.IsConfigured) return;

        var clients = await _db.Clients
            .Where(c => clientIds.Contains(c.Id) && c.PortalAccessUtc != null)
            .ToListAsync(ct);

        foreach (var c in clients) await PublishAsync(c, ct: ct);
    }

    /// <summary>The statement's content without its timestamp: equal fingerprints mean nothing changed for the client.</summary>
    public static string Fingerprint(PortalClientStatement s)
    {
        var json = JsonSerializer.Serialize(s with { GeneratedUtc = default });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

/// <summary>
/// Clients whose account changed and should be sent to the portal again. Filled by
/// <see cref="ClientPortalChangeTracker"/> on every save; drained by <see cref="ClientPortalSyncService"/>.
/// Work happens only when something changed, so an idle app never wakes a paused database.
/// </summary>
public class ClientPortalQueue
{
    private readonly Channel<(Guid? ClientId, Guid? LoanId)> _channel =
        Channel.CreateBounded<(Guid?, Guid?)>(new BoundedChannelOptions(5_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Client(Guid id) => _channel.Writer.TryWrite((id, null));
    public void Loan(Guid id) => _channel.Writer.TryWrite((null, id));

    public ChannelReader<(Guid? ClientId, Guid? LoanId)> Reader => _channel.Reader;
}

/// <summary>Notices saves that change what a client sees: loans, instalments, payments, debit orders, cell numbers.</summary>
public class ClientPortalChangeTracker : SaveChangesInterceptor
{
    private readonly ClientPortalQueue _queue;
    public ClientPortalChangeTracker(ClientPortalQueue queue) => _queue = queue;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null) return;
        foreach (var e in context.ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (e.Entity)
            {
                case Loan l: _queue.Client(l.ClientId); break;
                case DebiCheckMandate m: _queue.Client(m.ClientId); break;
                case ClientAddress a: _queue.Client(a.ClientId); break;
                case LoanCollection c: _queue.Loan(c.LoanId); break;
                case LoanScheduleEntry s: _queue.Loan(s.LoanId); break;
                case LoanContract k: _queue.Client(k.ClientId); break;
                case CreditQuote q: _queue.Client(q.ClientId); break;
            }
        }
    }
}

/// <summary>Sends changed client accounts to the portal a few seconds after the change, in batches.</summary>
public class ClientPortalSyncService : BackgroundService
{
    private static readonly TimeSpan Gather = TimeSpan.FromSeconds(3);

    private readonly ClientPortalQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClientPortalSyncService> _log;

    public ClientPortalSyncService(ClientPortalQueue queue, IServiceScopeFactory scopes, ILogger<ClientPortalSyncService> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken)) return;
                await Task.Delay(Gather, stoppingToken);

                var clients = new HashSet<Guid>();
                var loans = new HashSet<Guid>();
                while (reader.TryRead(out var item))
                {
                    if (item.ClientId is { } c) clients.Add(c);
                    if (item.LoanId is { } l) loans.Add(l);
                }

                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<ClientPortalPublisher>();
                if (!publisher.IsConfigured) continue;

                if (loans.Count > 0)
                    clients.UnionWith(await db.Loans.Where(l => loans.Contains(l.Id)).Select(l => l.ClientId).ToListAsync(stoppingToken));

                await publisher.PublishClientsAsync(clients, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The next change, or "Update now" on the client's page, sends it again.
                _log.LogWarning("Client portal update skipped: {Message}", ex.GetBaseException().Message);
            }
        }
    }
}
