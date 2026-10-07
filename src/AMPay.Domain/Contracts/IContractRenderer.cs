using AMPay.Domain.Enums;

namespace AMPay.Domain.Contracts;

/// <summary>How a pack was signed, printed on the signed copy's signature page.</summary>
public record ContractSignatureRecord(
    SignatureMethod Method,
    DateTime SignedUtc,
    string SignedName,
    string? SignedIdNumber,
    string? MaskedMobile,
    string? IpAddress,
    string? RecordedBy);

/// <summary>Turns a frozen snapshot into a PDF. The HTML view reads the same snapshot.</summary>
public interface IContractRenderer
{
    /// <param name="snapshotHash">Printed in the footer so a printed page can be matched to the record.</param>
    /// <param name="signature">Adds a signature page when the pack has been signed.</param>
    byte[] RenderPdf(ContractSnapshot snapshot, string snapshotHash, ContractSignatureRecord? signature = null);
}

public static class ContractFormat
{
    /// <summary>South African Standard Time has no daylight saving: a fixed two hours ahead.</summary>
    public static DateTime ToSast(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(2);

    public static string Money(decimal amount) => "R " + amount.ToString("N2");

    public static string Percent(decimal fraction, int decimals = 2) =>
        (fraction * 100m).ToString("N" + decimals) + "%";

    public static string Date(DateTime d) => d.ToString("dd MMM yyyy");

    public static string SastTime(DateTime utc) => ToSast(utc).ToString("dd MMM yyyy HH:mm") + " SAST";

    /// <summary>The first twelve characters of the fingerprint: enough to match, short enough to read.</summary>
    public static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash.ToUpperInvariant() : hash[..12].ToUpperInvariant();

    public static string MaskId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var t = id.Trim();
        return t.Length <= 6 ? new string('*', t.Length) : t[..6] + new string('*', t.Length - 6);
    }

    public static string MaskMobile(string? mobile)
    {
        if (string.IsNullOrWhiteSpace(mobile)) return "";
        var digits = new string(mobile.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? "****" : new string('*', digits.Length - 4) + digits[^4..];
    }

    /// <summary>Template bodies are plain text: blank lines separate paragraphs.</summary>
    public static IReadOnlyList<string> Paragraphs(string body) =>
        body.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => string.Join(' ', p.Split('\n', StringSplitOptions.TrimEntries)))
            .ToList();
}
