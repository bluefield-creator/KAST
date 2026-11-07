using System.Text.RegularExpressions;

namespace KAST.Tests;

/// <summary>
/// Keeps the CASTER theme honest: colours live in Themes/CasterTheme.cs (and
/// the console palette in wwwroot/css/caster.css), never in components; the
/// retired Artoria vocabulary stays retired; and the UI loads no assets from
/// third-party CDNs (panels run on firewalled and air-gapped hosts).
/// </summary>
public partial class ThemeGuardRailTests
{
    private static readonly string UiRoot = Path.Combine(FindRepoRoot(), "src", "KAST.UI");

    [GeneratedRegex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{3}\b(?![\w-])|\brgba?\(\s*\d", RegexOptions.CultureInvariant)]
    private static partial Regex HardCodedColor();

    [GeneratedRegex(@"artoria|kepi|brass|indigo|crimson|tunic|ribbon|aubergine|full-dress|castoria|--sheen|--mist-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetiredVocabulary();

    public static TheoryData<string> ComponentFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(UiRoot, "Components"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.css", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(UiRoot, file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ComponentFiles))]
    public void Components_UseThemeTokensInsteadOfColours(string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(UiRoot, relativePath));
        var offenders = lines
            .Select((line, i) => (line, number: i + 1))
            // App.razor hosts the chart helper JS that builds rgba() strings at runtime.
            .Where(l => !(relativePath.EndsWith("App.razor", StringComparison.Ordinal) && l.line.Contains("'rgba('", StringComparison.Ordinal)))
            .Where(l => HardCodedColor().IsMatch(l.line))
            .Select(l => $"{relativePath}:{l.number}: {l.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use --k-* / --mud-palette-* variables instead of hard-coded colours:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [MemberData(nameof(ComponentFiles))]
    public void Components_DoNotUseRetiredThemeVocabulary(string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(UiRoot, relativePath));
        var offenders = lines
            .Select((line, i) => (line, number: i + 1))
            .Where(l => RetiredVocabulary().IsMatch(l.line))
            .Select(l => $"{relativePath}:{l.number}: {l.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Retired Artoria theme reference:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ThemeStylesheet_HasNoRetiredVocabulary()
    {
        var css = File.ReadAllText(Path.Combine(UiRoot, "wwwroot", "css", "caster.css"));
        Assert.DoesNotMatch(RetiredVocabulary(), css);
    }

    [Fact]
    public void App_LoadsNoThirdPartyAssets()
    {
        var app = File.ReadAllText(Path.Combine(UiRoot, "Components", "App.razor"));
        foreach (var host in new[] { "googleapis.com", "gstatic.com", "jsdelivr.net", "unpkg.com", "cdnjs.cloudflare.com" })
            Assert.DoesNotContain(host, app, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VendoredAssets_ArePresent()
    {
        foreach (var asset in new[]
                 {
                     "lib/fonts/Inter-Regular.woff2", "lib/fonts/Inter-SemiBold.woff2", "lib/fonts/JetBrainsMono-Regular.woff2",
                     "lib/chartjs/chart.umd.min.js", "brand/logo-64.png", "favicon.ico"
                 })
        {
            Assert.True(File.Exists(Path.Combine(UiRoot, "wwwroot", asset)), $"Missing wwwroot/{asset}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KAST.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (KAST.slnx) not found.");
    }
}
