namespace AMPay.Tests;

/// <summary>
/// The ID reader script is served by both apps. They must ship the same file, or a fix made in
/// one quietly never reaches the other.
/// </summary>
public class IdReaderScriptTests
{
    [Fact]
    public void BothAppsServeTheSameIdReader()
    {
        var root = FindRepoRoot();
        var web = File.ReadAllText(Path.Combine(root, "src", "AMPay.Web", "wwwroot", "js", "id-reader.js"));
        var portal = File.ReadAllText(Path.Combine(root, "src", "AMPay.Portal", "wwwroot", "js", "id-reader.js"));

        Assert.Equal(web.ReplaceLineEndings(), portal.ReplaceLineEndings());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AMPay.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
