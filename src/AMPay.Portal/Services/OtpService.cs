using System.Security.Cryptography;
using System.Text;
using AMPay.Portal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AMPay.Portal.Services;

/// <summary>
/// Sign-in by one-time code to a cell number. The number is the client's identity here:
/// whoever can read the SMS can see and change the application.
/// </summary>
public class OtpService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 5;

    /// <summary>Codes one number may be sent per hour - SMS costs money and annoys people.</summary>
    public const int MaxCodesPerHour = 5;

    private readonly PortalDbContext _db;
    private readonly PortalOptions _options;
    private readonly ILogger<OtpService> _log;

    public OtpService(PortalDbContext db, IOptions<PortalOptions> options, ILogger<OtpService> log)
    {
        _db = db;
        _options = options.Value;
        _log = log;
    }

    public record Issued(Guid ChallengeId, string? TestCode);

    /// <summary>Creates a code and sends it. In test mode, returns it for display instead.</summary>
    public async Task<(Issued? Result, string? Error)> IssueAsync(Lender lender, string mobile, string? ip, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var recent = await _db.OtpChallenges
            .Where(o => o.LenderId == lender.Id && o.Mobile == mobile && o.CreatedUtc > now.AddHours(-1))
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => o.CreatedUtc)
            .ToListAsync(ct);

        if (recent.Count >= MaxCodesPerHour)
            return (null, "Too many codes have been sent to this number. Try again in an hour.");
        if (recent.Count > 0 && now - recent[0] < ResendInterval)
            return (null, "A code was sent less than a minute ago. Wait a moment before asking for another.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new OtpChallenge
        {
            LenderId = lender.Id,
            Mobile = mobile,
            CodeHash = Hash(Guid.Empty, code), // replaced below once the id exists
            ExpiresUtc = now + CodeLifetime,
            RequestIp = ip
        };
        challenge.CodeHash = Hash(challenge.Id, code);

        _db.OtpChallenges.Add(challenge);
        await _db.SaveChangesAsync(ct);

        if (_options.UseStubs)
        {
            _log.LogWarning("TEST MODE: sign-in code for {Lender} shown on screen, not sent by SMS.", lender.PublicCode);
            return (new Issued(challenge.Id, code), null);
        }

        // GAP - SMS provider. Send "{code} is your {lender} sign-in code" to the number here.
        throw new InvalidOperationException("Portal:UseStubs is false but no SMS provider is connected.");
    }

    /// <summary>Checks a code. Returns the verified number, or an error to show.</summary>
    public async Task<(string? Mobile, string? Error)> VerifyAsync(Guid challengeId, Guid lenderId, string? code, CancellationToken ct = default)
    {
        var c = await _db.OtpChallenges.FirstOrDefaultAsync(o => o.Id == challengeId && o.LenderId == lenderId, ct);
        if (c is null || c.ConsumedUtc is not null) return (null, "Ask for a new code.");
        if (c.ExpiresUtc < DateTime.UtcNow) return (null, "The code has expired. Ask for a new one.");
        if (c.FailedAttempts >= MaxAttempts) return (null, "Too many wrong codes. Ask for a new one.");

        var ok = !string.IsNullOrWhiteSpace(code) &&
                 CryptographicOperations.FixedTimeEquals(
                     Encoding.ASCII.GetBytes(Hash(c.Id, code.Trim())),
                     Encoding.ASCII.GetBytes(c.CodeHash));

        if (!ok)
        {
            c.FailedAttempts++;
            await _db.SaveChangesAsync(ct);
            return (null, c.FailedAttempts >= MaxAttempts ? "Too many wrong codes. Ask for a new one." : "That code is not right.");
        }

        c.ConsumedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (c.Mobile, null);
    }

    private static string Hash(Guid id, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id:N}:{code}"))).ToLowerInvariant();
}
