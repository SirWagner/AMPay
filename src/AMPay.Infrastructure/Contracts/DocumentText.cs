using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AMPay.Infrastructure.Contracts;

/// <summary>
/// Pulls the plain text out of an attorney's document so it can be dropped into the contract
/// wording editor. Word (.docx) and plain text only: a .docx is a zip of XML, readable with what
/// .NET already has. PDF is not handled - there is no reliable way to get paragraphs back out of
/// one without a paid library, and a half-right extraction of legal wording is worse than none.
/// </summary>
public static class DocumentText
{
    /// <summary>Largest upload read. Contract wording is a few pages; this is generous.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    public static readonly string[] Extensions = { ".docx", ".txt" };

    /// <summary>The text, or null with the reason it could not be read.</summary>
    public static (string? Text, string? Error) Extract(string fileName, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var text = extension switch
            {
                ".docx" => FromDocx(content),
                ".txt" => FromText(content),
                ".pdf" => null,
                ".doc" => null,
                _ => null
            };

            if (text is null)
                return (null, extension switch
                {
                    ".pdf" => "Text cannot be pulled out of a PDF reliably. Save it as a Word document (.docx), or copy the text and paste it in.",
                    ".doc" => "That is the old Word format. Open it in Word, save it as .docx, and upload that.",
                    _ => "Upload a Word document (.docx) or a plain text file (.txt)."
                });

            text = Tidy(text);
            return text.Length == 0 ? (null, "No text was found in that document.") : (text, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            return (null, "That file could not be read. Check that it is a real Word document and not password-protected.");
        }
    }

    private static string FromText(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? FromDocx(Stream content)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return null;

        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var body = doc.Root?.Element(w + "body");
        if (body is null) return "";

        var sb = new StringBuilder();
        // Every paragraph, including those inside tables, in document order: one line each.
        foreach (var p in body.Descendants(w + "p"))
        {
            foreach (var node in p.Descendants())
            {
                if (node.Name == w + "t") sb.Append(node.Value);
                else if (node.Name == w + "tab") sb.Append(' ');
                else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Trims each line and squeezes runs of blank lines to one, as the editor expects.</summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ')
            .Split('\n').Select(l => l.TrimEnd());

        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            blank = line.Length == 0 ? blank + 1 : 0;
            if (blank > 1) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
