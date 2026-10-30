using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AMPay.Web.Services;

/// <summary>
/// The statutory caps in force on a date, from the effective-dated <see cref="RegulatoryCap"/>
/// table. A cap with no row uses the statutory default, except the unsecured interest cap,
/// which has no default because it moves with the repo rate.
/// </summary>
public class RegulatoryCapsProvider
{
    /// <summary>The date the seeded defaults are recorded as applying from (the 2016 fee regulations).</summary>
    public static readonly DateTime SeedEffectiveFrom = new(2016, 5, 6);

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "regulatory-caps:";

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public RegulatoryCapsProvider(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<RegulatoryCaps> ForDateAsync(DateTime sastDate, CancellationToken ct = default)
    {
        var date = sastDate.Date;
        return (await _cache.GetOrCreateAsync(CacheKeyPrefix + date.ToString("yyyyMMdd"), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            var rows = await _db.RegulatoryCaps.AsNoTracking()
                .Where(c => c.EffectiveFrom <= date && (c.EffectiveTo == null || c.EffectiveTo > date))
                .OrderBy(c => c.EffectiveFrom)
                .ToListAsync(ct);
            return Apply(rows);
        }))!;
    }

    /// <summary>Later rows win over earlier ones of the same type.</summary>
    public static RegulatoryCaps Apply(IEnumerable<RegulatoryCap> rows) =>
        rows.OrderBy(r => r.EffectiveFrom).Aggregate(RegulatoryCaps.Statutory, (caps, r) => caps.With(r.Type, r.Value));

    /// <summary>
    /// Records a new value from a date. The current row of that type ends the day before, so the
    /// history stays intact and loans priced earlier keep their caps.
    /// </summary>
    public async Task SetAsync(CapType type, decimal value, DateTime effectiveFrom, string? source, string? userId, CancellationToken ct = default)
    {
        var from = effectiveFrom.Date;
        var open = await _db.RegulatoryCaps
            .Where(c => c.Type == type && c.EffectiveTo == null && c.EffectiveFrom < from)
            .ToListAsync(ct);
        foreach (var row in open) row.EffectiveTo = from;

        // Replacing a value that starts on the same day: the newer entry wins outright.
        var sameDay = await _db.RegulatoryCaps.Where(c => c.Type == type && c.EffectiveFrom == from).ToListAsync(ct);
        _db.RegulatoryCaps.RemoveRange(sameDay);

        _db.RegulatoryCaps.Add(new RegulatoryCap
        {
            Type = type,
            Value = value,
            EffectiveFrom = from,
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            CreatedByUserId = userId
        });
        await _db.SaveChangesAsync(ct);
        Forget();
    }

    /// <summary>Drops cached caps after a change.</summary>
    public void Forget()
    {
        // Cached by date; the cache is small and short-lived, so compacting it is simplest.
        if (_cache is MemoryCache mc) mc.Compact(1.0);
    }

    /// <summary>Seeds the statutory defaults when the table is empty. Called at start-up.</summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.RegulatoryCaps.AnyAsync(ct)) return;

        var statutory = RegulatoryCaps.Statutory;
        foreach (var type in Enum.GetValues<CapType>())
        {
            if (statutory.Get(type) is not { } value) continue; // the unsecured cap has no default
            db.RegulatoryCaps.Add(new RegulatoryCap
            {
                Type = type,
                Value = value,
                EffectiveFrom = SeedEffectiveFrom,
                Source = "Statutory default seeded by AM-Pay - verify against the current regulations"
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
