using System.Security.Cryptography;
using AMPay.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Documents;

/// <summary>
/// Stores documents on the local filesystem, partitioned by tenant and client.
/// <para>
/// GAP - production storage. Replace with an Azure Blob implementation of
/// <see cref="IDocumentStore"/> (private container, server-side encryption, soft delete)
/// and register that instead. Local disk is fine for development and single-server
/// deployments; it is not fine once the app scales out, because the second instance cannot
/// see the first one's files.
/// </para>
/// </summary>
public class LocalDocumentStore : IDocumentStore
{
    private readonly DocumentStorageOptions _options;
    private readonly ILogger<LocalDocumentStore> _log;
    private readonly string _root;

    public LocalDocumentStore(
        IOptions<DocumentStorageOptions> options,
        ILogger<LocalDocumentStore> log)
    {
        _options = options.Value;
        _log = log;

        _root = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredDocument> SaveAsync(
        Guid tenantId, Guid clientId, string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;

        if (!_options.AllowedExtensions.Contains(extension))
            throw new InvalidOperationException(
                $"Files of type {(string.IsNullOrEmpty(extension) ? "(none)" : extension)} are not accepted.");

        // The stored name is ours, not theirs. A file called "..\\..\\web.config" or
        // "invoice.pdf.exe" stops being interesting once it is renamed to a GUID.
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var relative = Path.Combine(tenantId.ToString("N"), clientId.ToString("N"), storedName);
        var absolute = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        long size;
        string hash;

        await using (var target = File.Create(absolute))
        {
            using var sha = SHA256.Create();
            await using var tee = new CryptoStream(target, sha, CryptoStreamMode.Write);

            await content.CopyToAsync(tee, ct);
            await tee.FlushFinalBlockAsync(ct);

            size = target.Length;
            hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        if (size > _options.MaxFileSizeBytes)
        {
            // Length is only known once written. Delete rather than leave an oversized
            // orphan on disk.
            File.Delete(absolute);
            throw new InvalidOperationException(
                $"The file is {size / 1024d / 1024d:N1} MB. The limit is " +
                $"{_options.MaxFileSizeBytes / 1024d / 1024d:N0} MB.");
        }

        if (size == 0)
        {
            File.Delete(absolute);
            throw new InvalidOperationException("The file is empty.");
        }

        _log.LogInformation(
            "Stored document {Path} ({Size} bytes) for client {ClientId}.", relative, size, clientId);

        // Relative, and always with forward slashes, so the stored value survives a move to
        // blob storage and a move between operating systems.
        return new StoredDocument(relative.Replace('\\', '/'), hash, size);
    }

    public Task<Stream?> OpenAsync(string storagePath, CancellationToken ct = default)
    {
        var absolute = Resolve(storagePath);

        if (absolute is null || !File.Exists(absolute))
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(
            new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var absolute = Resolve(storagePath);

        if (absolute is not null && File.Exists(absolute))
            File.Delete(absolute);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a stored path into an absolute one, refusing anything that escapes the root.
    /// <para>
    /// The stored path comes out of the database, but the database is not a trust boundary -
    /// a path traversal written in through some other defect must not become an arbitrary
    /// file read here.
    /// </para>
    /// </summary>
    private string? Resolve(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) return null;

        var candidate = Path.GetFullPath(Path.Combine(_root, storagePath));

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Refused a document path that escapes the store root: {Path}.", storagePath);
            return null;
        }

        return candidate;
    }
}
