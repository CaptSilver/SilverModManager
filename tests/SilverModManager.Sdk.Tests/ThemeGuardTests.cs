using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// The theme project is SilverModManager.Themes.Silver. The name it had when it was forked must
/// not come back in a path, a project reference, a resource URI or the docs.
/// </summary>
public partial class ThemeGuardTests
{
    // Assembled from pieces so this file does not contain the names it forbids.
    private static readonly string[] OldNames = ["Themes." + "NexusFluent" + "Dark", "NexusFluent" + "DarkTheme"];

    private const string ThisFile = "tests/SilverModManager.Sdk.Tests/ThemeGuardTests.cs";

    [Fact]
    public void Test_NoTrackedFileNamesTheOldThemeProject()
    {
        var root = RepoScanGuard.FindRepoRoot();

        var hits = RepoScanGuard.TrackedFiles(root)
            .Where(path => path != ThisFile)
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Where(path => OldNames.Any(name => path.Contains(name, StringComparison.Ordinal))
                           || OldNames.Any(name => File.ReadAllText(Path.Combine(root, path)).Contains(name, StringComparison.Ordinal)))
            .Order();

        string.Join(Environment.NewLine, hits).Should().BeEmpty("the theme project was renamed to SilverModManager.Themes.Silver");
    }

    private static readonly string[] OrangeFolders =
    [
        "src/SilverModManager.Themes.Silver",
        "src/SilverModManager.App.UI",
        "src/SilverModManager.Games.FOMOD.UI",
        "src/SilverModManager.Games.AdvancedInstaller.UI",
    ];

    private const string PrimitiveColorsFile = "src/SilverModManager.Themes.Silver/Resources/Palette/Colors/PrimitiveColors.axaml";

    [GeneratedRegex("""<Color x:Key="PrimitiveOrange\d+">#(?<hex>[0-9a-fA-F]{6})</Color>""")]
    private static partial Regex OrangeSwatch();

    [Fact]
    public void Test_NoOrangeBrandHexInAnyTrackedAxaml()
    {
        var root = RepoScanGuard.FindRepoRoot();

        // The forked brand's orange, plus every shade of the primitive orange ramp. The ramp itself stays as a
        // swatch table, so its own definitions are the one place these values may appear.
        var swatches = OrangeSwatch().Matches(File.ReadAllText(Path.Combine(root, PrimitiveColorsFile)))
            .Select(match => match.Groups["hex"].Value)
            .ToArray();
        swatches.Should().NotBeEmpty("the scan reads its shades from the primitive orange ramp");
        var orange = new Regex(
            "#(?:[0-9a-fA-F]{2})?(?:" + string.Join("|", swatches.Append("FB923C").Append("FF8F28").Distinct(StringComparer.OrdinalIgnoreCase)) + @")\b",
            RegexOptions.IgnoreCase);

        var files = RepoScanGuard.TrackedFiles(root, OrangeFolders.Select(folder => folder + "/**/*.axaml").ToArray())
            .Where(path => File.Exists(Path.Combine(root, path)));
        var hits = RepoScanGuard.FindLines(root, files, line => orange.IsMatch(line) && !OrangeSwatch().IsMatch(line))
            .Select(hit => $"{hit.File}: {hit.Line}")
            .Order();

        string.Join(Environment.NewLine, hits).Should().BeEmpty("the orange accent is gone, colours come from the palette");
    }

    [GeneratedRegex("""<ColorPaletteResources\b.*?/>""", RegexOptions.Singleline)]
    private static partial Regex PaletteElement();

    [GeneratedRegex("""#[0-9a-fA-F]{6,8}\b""")]
    private static partial Regex HexLiteral();

    [Fact]
    public void Test_FluentPaletteHasNoHandPickedBlueSamples()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src/SilverModManager.Themes.Silver/SilverTheme.axaml"));

        var palette = PaletteElement().Match(xaml);
        palette.Success.Should().BeTrue("SilverTheme.axaml declares the Fluent ColorPaletteResources");

        // The sample blues (#ff8dbfdf and its neighbours) came with the template. Every colour is
        // taken from the palette ramps instead, so a literal in here is a leftover.
        HexLiteral().Matches(palette.Value).Select(match => match.Value).Should().BeEmpty();
    }
}
