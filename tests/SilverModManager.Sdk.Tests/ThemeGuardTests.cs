using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// The theme project is SilverModManager.Themes.Silver. The name it had when it was forked must
/// not come back in a path, a project reference, a resource URI or the docs.
/// </summary>
public class ThemeGuardTests
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
}
