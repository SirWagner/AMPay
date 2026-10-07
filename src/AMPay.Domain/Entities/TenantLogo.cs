namespace AMPay.Domain.Entities;

/// <summary>
/// A lender's logo, shown in their Loan Flow and on their self-service portal.
/// <para>
/// Its own table, keyed by tenant, so the bytes are read only when the image itself is
/// requested - never on the many queries that load a tenant.
/// </para>
/// </summary>
public class TenantLogo
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string ContentType { get; set; } = "image/png";
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }

    public const int MaxBytes = 300 * 1024;

    /// <summary>
    /// The image type from the file's first bytes, or null when it is not a PNG, JPEG or WebP.
    /// SVG is refused on purpose: it can carry script.
    /// </summary>
    public static string? SniffContentType(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }
}
