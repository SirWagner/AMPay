using System.Collections.Concurrent;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Asks Netcash which eMandates have been signed, so a postback that never arrives does not leave
/// a signed mandate waiting. Read-only at Netcash; only the reference and status are used.
/// </summary>
public class EMandateChecker
{
    private readonly AppDbContext _db;
    private readonly INetcashDebitOrderService _netcash;
    private readonly INetcashSecretStore _secrets;

    public EMandateChecker(AppDbContext db, INetcashDebitOrderService netcash, INetcashSecretStore secrets)
    {
        _db = db;
        _netcash = netcash;
        _secrets = secrets;
    }

    public record Outcome(int Updated, int StillWaiting, string? Error);

    public async Task<Outcome> CheckAsync(Guid tenantId, CancellationToken ct = default)
    {
        var all = await _db.Mandates
            .Where(m => m.TenantId == tenantId && m.MandateType == MandateType.NetcashEMandate)
            .OrderByDescending(m => m.CreatedUtc).ToListAsync(ct);
        var waiting = all.Where(m => m.Status == MandateStatus.AwaitingDebtorAuthentication).ToList();
        if (waiting.Count == 0) return new Outcome(0, 0, null);

        var key = await KeyAsync(_db, _secrets, tenantId, ct);
        var result = await _netcash.GetMandateStatusesAsync(key ?? string.Empty, ct);
        if (!result.Success || result.Data is null) return new Outcome(0, waiting.Count, result.Message);

        var updated = 0;
        foreach (var m in waiting)
        {
            // The reference is all Netcash gives back. If an earlier mandate on it was signed, its
            // answer cannot be told apart from this one's: leave it to the postback.
            if (all.Any(o => o.Id != m.Id && o.AccountReference == m.AccountReference && o.AuthenticatedUtc != null)) continue;

            var to = Decide(result.Data.Where(s => s.AccountReference == m.AccountReference).Select(s => s.Status).ToList());
            if (to is null) continue;

            _db.MandateEvents.Add(new MandateEvent
            {
                MandateId = m.Id, EventType = "eMandateChecked", FromStatus = m.Status, ToStatus = to,
                Detail = "Status read from Netcash's mandate data."
            });
            m.Status = to.Value;
            if (to == MandateStatus.Authenticated)
            {
                m.AuthenticatedUtc = DateTime.UtcNow;
                m.ResponseMessage = "Signed by the account holder";
            }
            updated++;
        }

        if (updated > 0) await _db.SaveChangesAsync(ct);
        return new Outcome(updated, waiting.Count - updated, null);
    }

    /// <summary>
    /// Netcash statuses: 6 accepted, 5 declined, 3 expired, 4 failed; anything else is unfinished.
    /// Null means keep waiting. A re-sent mandate shares its reference with the one it replaced,
    /// so one still open outweighs one declined.
    /// </summary>
    public static MandateStatus? Decide(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Contains("6")) return MandateStatus.Authenticated;
        if (statuses.Count == 0 || statuses.Any(s => s is not ("3" or "4" or "5"))) return null;
        return statuses.Contains("5") ? MandateStatus.Rejected
            : statuses.Contains("3") ? MandateStatus.Expired : MandateStatus.Failed;
    }

    /// <summary>A business's own debit order key, whichever business the user has selected.</summary>
    public static async Task<string?> KeyAsync(AppDbContext db, INetcashSecretStore secrets, Guid tenantId, CancellationToken ct = default)
    {
        var t = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId)
            .Select(x => new { x.NetcashAccountNumber, Key = x.ServiceKeys.Where(k => k.ServiceId == NetcashServiceId.DebitOrders).Select(k => new { k.SecretName, k.IsActive }).FirstOrDefault() })
            .FirstAsync(ct);
        return t.Key is { IsActive: false } ? null
            : await secrets.ResolveServiceKeyAsync(t.Key?.SecretName ?? "", t.NetcashAccountNumber, NetcashServiceId.DebitOrders, ct);
    }
}

/// <summary>After an eMandate is sent, checks on it for about an hour. Nothing runs otherwise.</summary>
public class EMandateWatcher
{
    private static readonly int[] MinutesBetween = { 1, 2, 3, 5, 5, 10, 15, 20 };

    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _life;
    private readonly ILogger<EMandateWatcher> _log;
    private readonly ConcurrentDictionary<Guid, byte> _watching = new();

    public EMandateWatcher(IServiceScopeFactory scopes, IHostApplicationLifetime life, ILogger<EMandateWatcher> log)
    {
        _scopes = scopes;
        _life = life;
        _log = log;
    }

    public void Watch(Guid tenantId)
    {
        if (_watching.TryAdd(tenantId, 0)) _ = Task.Run(() => RunAsync(tenantId, _life.ApplicationStopping));
    }

    private async Task RunAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            foreach (var minutes in MinutesBetween)
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
                try
                {
                    using var scope = _scopes.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<EMandateChecker>().CheckAsync(tenantId, ct);
                    if (outcome.StillWaiting == 0) return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("eMandate check skipped: {Message}", ex.GetBaseException().Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _watching.TryRemove(tenantId, out _); }
    }
}
