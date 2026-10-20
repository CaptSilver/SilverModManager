using System.Text.RegularExpressions;
using FluentAssertions;

namespace NexusMods.UI.Tests;

/// <summary>
/// Avalonia resolves <c>avares://</c> URIs when a control is first shown, not at build time, so a
/// deleted or renamed asset only fails once someone opens that screen. This walks every literal
/// asset reference in source and checks the file is still there.
/// </summary>
public partial class AssetReferenceTests
{
    // "avares://<assembly>/Assets/<path>" and the window-relative form Icon="/Assets/<path>".
    [GeneratedRegex("""avares://(?<project>[A-Za-z0-9_.]+)/(?<path>Assets/[^"'<>)\s{}]+)|\bIcon="/(?<local>Assets/[^"{}]+)""")]
    private static partial Regex AssetReference();

    [Fact]
    public void Test_EveryAssetReferenceResolvesToAFile()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        var sources = new[] { "*.axaml", "*.cs" }
            .SelectMany(pattern => Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();

        var checkedCount = 0;
        var dangling = new List<string>();
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in AssetReference().Matches(text))
            {
                // A bare Icon="/Assets/..." belongs to the project the XAML file lives in.
                var projectDirectory = match.Groups["project"].Success
                    ? Path.Combine(src, match.Groups["project"].Value)
                    : OwningProjectDirectory(file);
                var path = match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["local"].Value;

                checkedCount++;
                if (!File.Exists(Path.Combine(projectDirectory, path)))
                    dangling.Add($"{Path.GetRelativePath(root, file)} -> {match.Value}");
            }
        }

        checkedCount.Should().BeGreaterThan(0, "the scan must find references, otherwise the pattern is broken");
        dangling.Should().BeEmpty("every asset reference must point at an existing file");
    }

    private static string OwningProjectDirectory(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir is not null && dir.GetFiles("*.csproj").Length == 0) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"No project file above {file}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexusMods.App.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find NexusMods.App.sln above the test binaries");
    }
}
