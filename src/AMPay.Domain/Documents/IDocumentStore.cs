namespace AMPay.Domain.Documents;

/// <summary>What the store hands back once a file is safely written.</summary>
public record StoredDocument(string StoragePath, string ContentHash, long SizeBytes);

/// <summary>
/// Where supporting documents physically live.
/// <para>
/// Deliberately an interface over a folder today and Azure Blob Storage tomorrow, in the
/// same spirit as the INetcash* contracts: nothing above this line changes when the
/// implementation is swapped.
/// </para>
/// <para>
/// A stored file is never addressable by URL. Callers receive a storage path and must
/// stream the bytes back through an authorised action - an ID document sitting behind a
/// guessable public link is a POPIA breach waiting to be reported.
/// </para>
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Writes <paramref name="content"/> and returns where it landed, its SHA-256 and its
    /// size. The original file name is not used as the stored name - it is attacker
    /// controlled and only meaningful to the person who uploaded it.
    /// </summary>
    Task<StoredDocument> SaveAsync(
        Guid tenantId, Guid clientId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Opens a stored file, or null when it is missing.</summary>
    Task<Stream?> OpenAsync(string storagePath, CancellationToken ct = default);

    Task DeleteAsync(string storagePath, CancellationToken ct = default);
}

/// <summary>
/// What may be uploaded and where it goes. The allow-lists are deliberately narrow:
/// a lender's document store is a favourite place to park a web shell.
/// </summary>
public class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";

    /// <summary>
    /// Root folder for stored files. Must sit OUTSIDE wwwroot - anything under wwwroot is
    /// served as a static file by the framework, with no authorisation at all.
    /// </summary>
    public string RootPath { get; set; } = "App_Data/documents";

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    public List<string> AllowedExtensions { get; set; } = new()
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".heic"
    };

    public List<string> AllowedContentTypes { get; set; } = new()
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic"
    };
}
