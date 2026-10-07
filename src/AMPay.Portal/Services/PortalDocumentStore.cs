using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace AMPay.Portal.Services;

/// <summary>
/// Stores uploads on disk under a name of our choosing, after checking that the bytes are
/// what the extension claims. Files are only ever streamed back through AM-Pay's API call,
/// never by URL.
/// </summary>
public class PortalDocumentStore
{
    private static readonly Dictionary<string, (string ContentType, byte[][] Signatures)> Allowed = new()
    {
        [".pdf"] = ("application/pdf", new[] { "%PDF"u8.ToArray() }),
        [".jpg"] = ("image/jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } }),
        [".jpeg"] = ("image/jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } }),
        [".png"] = ("image/png", new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } })
    };

    public static IReadOnlyCollection<string> Extensions => Allowed.Keys;

    private readonly PortalOptions _options;
    private readonly string _root;

    public PortalDocumentStore(IOptions<PortalOptions> options)
    {
        _options = options.Value;
        _root = Path.GetFullPath(_options.DocumentRoot);
        Directory.CreateDirectory(_root);
    }

    public record Stored(string StoragePath, string ContentType, long SizeBytes, string Sha256);

    public async Task<(Stored? Result, string? Error)> SaveAsync(Guid lenderId, Guid applicationId, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0) return (null, "The file is empty.");
        if (file.Length > _options.MaxFileSizeBytes)
            return (null, $"The file is larger than {_options.MaxFileSizeBytes / (1024 * 1024)} MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Allowed.TryGetValue(ext, out var kind))
            return (null, "Upload a PDF, JPG or PNG file.");

        // The extension is the client's claim; the first bytes are the evidence.
        var head = new byte[8];
        await using (var peek = file.OpenReadStream())
        {
            var read = await peek.ReadAsync(head, ct);
            if (!kind.Signatures.Any(sig => read >= sig.Length && head.AsSpan(0, sig.Length).SequenceEqual(sig)))
                return (null, "That file is not a real " + ext.TrimStart('.').ToUpperInvariant() + ". Upload the original document.");
        }

        var relative = Path.Combine(lenderId.ToString("N"), applicationId.ToString("N"), $"{Guid.NewGuid():N}{ext}");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        await using var input = file.OpenReadStream();
        await using var output = File.Create(absolute);
        using var sha = SHA256.Create();
        await using (var tee = new CryptoStream(output, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            await input.CopyToAsync(tee, ct);
        }

        return (new Stored(relative, kind.ContentType, file.Length, Convert.ToHexString(sha.Hash!).ToLowerInvariant()), null);
    }

    public Stream? Open(string storagePath)
    {
        var absolute = Path.GetFullPath(Path.Combine(_root, storagePath));
        // Never follow a stored path outside the root.
        if (!absolute.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(absolute)) return null;
        return File.OpenRead(absolute);
    }

    public void Delete(string storagePath)
    {
        var absolute = Path.GetFullPath(Path.Combine(_root, storagePath));
        if (absolute.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && File.Exists(absolute)) File.Delete(absolute);
    }
}
