using PdfSharp.Fonts;

namespace AMPay.Infrastructure.Contracts;

/// <summary>
/// Gives PDFsharp one sans-serif face for every contract, wherever the app runs.
/// <para>
/// Noto Sans ships with the application (Contracts/Fonts, SIL Open Font License - see
/// OFL.txt there), so a pack renders identically on a developer's Windows machine and on
/// an Azure App Service Linux plan, which has no Arial. The host's fonts are only a
/// fallback for a build that somehow lost the bundled files.
/// </para>
/// Every family the document asks for maps to that one face; contracts use one typeface.
/// </summary>
public sealed class ContractFontResolver : IFontResolver
{
    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "Contracts", "Fonts");

    private static readonly string WindowsFonts =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    // Regular, bold, italic, bold italic - first set found wins.
    private static readonly string[][] Candidates =
    {
        new[] { "NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf", "NotoSans-BoldItalic.ttf" }
            .Select(f => Path.Combine(Bundled, f)).ToArray(),
        new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" }
            .Select(f => Path.Combine(WindowsFonts, f)).ToArray(),
        new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Oblique.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-BoldOblique.ttf"
        }
    };

    private static readonly Lazy<string[]> Faces = new(() =>
        Candidates.FirstOrDefault(set => File.Exists(set[0]) && File.Exists(set[1]))
        ?? throw new InvalidOperationException(
            $"No font was found for contract PDFs. The bundled Noto Sans files are missing from {Bundled}."));

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var index = (bold ? 1 : 0) + (italic ? 2 : 0);
        var faces = Faces.Value;

        // Fall back to regular or bold when the italic files are missing; PDFsharp simulates the slant.
        if (!File.Exists(faces[index]))
            return new FontResolverInfo(bold ? "face1" : "face0", false, italic);

        return new FontResolverInfo($"face{index}");
    }

    public byte[]? GetFont(string faceName)
    {
        var index = int.Parse(faceName["face".Length..]);
        return File.ReadAllBytes(Faces.Value[index]);
    }
}
