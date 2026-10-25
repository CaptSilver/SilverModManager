using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Avalonia resolves <c>avares://</c> URIs when a control is first shown, not at build time, so a
/// deleted or renamed asset only fails once someone opens that screen. This walks every literal
/// <c>avares://</c> reference in source and checks that the host is a real assembly and the file is still
/// there. The host is an assembly name, so a project rename that misses one reference only shows up here.
/// </summary>
public partial class AssetReferenceTests
{
    // A full avares URI (host is the assembly name), and the window-relative form of an icon attribute.
    [GeneratedRegex("""avares://(?<project>[A-Za-z0-9_.]+)/(?<path>[^"'<>)\s{}]+)|\bIcon="/(?<local>Assets/[^"{}]+)""")]
    private static partial Regex AssetReference();

    // Hosts that are packages we depend on, not projects in this tree; their files are not ours to check.
    private static readonly string[] ThirdPartyHosts = ["Avalonia.Controls.DataGrid", "Avalonia.Controls.TreeDataGrid", "AvaloniaEdit"];

    [Fact]
    public void Test_EveryAssetReferenceResolvesToAFile()
    {
        var root = FindRepoRoot();
        var projects = ProjectsByAssemblyName(root);
        var sources = new[] { "src", "tests" }
            .SelectMany(top => new[] { "*.axaml", "*.cs" }
                .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(root, top), pattern, SearchOption.AllDirectories)))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();

        var checkedCount = 0;
        var dangling = new List<string>();
        foreach (var file in sources)
        {
            // Sources shared by every test project (tests/TestScratch.cs) belong to no project and hold no references.
            if (OwningProjectDirectory(file) is not { } owner) continue;
            var references = FindDanglingReferences(File.ReadAllText(file), owner, projects, out var found);
            checkedCount += found;
            dangling.AddRange(references.Select(reference => $"{Path.GetRelativePath(root, file)} -> {reference}"));
        }

        checkedCount.Should().BeGreaterThan(0, "the scan must find references, otherwise the pattern is broken");
        dangling.Should().BeEmpty("every avares:// host must be an assembly in this tree and every path must exist in it");
    }

    [Fact]
    public void Test_AnUnknownHostIsReported()
    {
        // Built in pieces so the scan above does not trip over this fixture.
        var bogus = "avares" + "://No.Such.Assembly/Assets/Icons/x.svg";
        var projects = ProjectsByAssemblyName(FindRepoRoot());

        FindDanglingReferences($"<Image Source=\"{bogus}\" />", FindRepoRoot(), projects, out var found)
            .Should().ContainSingle().Which.Should().Contain("No.Such.Assembly");
        found.Should().Be(1);
    }

    [Fact]
    public void Test_AMissingPathInARealProjectIsReported()
    {
        var projects = ProjectsByAssemblyName(FindRepoRoot());
        var host = projects.Keys.First(name => name.EndsWith(".App.UI", StringComparison.Ordinal));
        var reference = "avares" + $"://{host}/Pages/DoesNotExist.axaml";

        FindDanglingReferences($"Source=\"{reference}\"", FindRepoRoot(), projects, out _)
            .Should().ContainSingle().Which.Should().Contain("DoesNotExist");
    }

    private static List<string> FindDanglingReferences(string text, string owningProjectDirectory, Dictionary<string, string> projects, out int found)
    {
        found = 0;
        var dangling = new List<string>();
        foreach (Match match in AssetReference().Matches(text))
        {
            found++;
            string projectDirectory;
            if (match.Groups["project"].Success)
            {
                var host = match.Groups["project"].Value;
                if (ThirdPartyHosts.Contains(host)) continue;
                if (!projects.TryGetValue(host, out projectDirectory!))
                {
                    dangling.Add(match.Value);
                    continue;
                }
            }
            else
            {
                // A bare icon attribute with a rooted Assets path belongs to the project the XAML file lives in.
                projectDirectory = owningProjectDirectory;
            }

            var path = match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["local"].Value;
            if (!File.Exists(Path.Combine(projectDirectory, path))) dangling.Add(match.Value);
        }
        return dangling;
    }

    /// <summary>
    /// Every project in the tree by the name its assembly gets: the csproj's AssemblyName when it sets one
    /// (the app does), otherwise the file name.
    /// </summary>
    private static Dictionary<string, string> ProjectsByAssemblyName(string root)
    {
        var projects = new Dictionary<string, string>();
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
        {
            var name = XDocument.Load(csproj).Descendants("AssemblyName").FirstOrDefault()?.Value.Trim()
                       ?? Path.GetFileNameWithoutExtension(csproj);
            projects[name] = Path.GetDirectoryName(csproj)!;
        }
        return projects;
    }

    private static string? OwningProjectDirectory(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir is not null && dir.GetFiles("*.csproj").Length == 0) dir = dir.Parent;
        return dir?.FullName;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, FirstPartyNames.SolutionFile))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not find {FirstPartyNames.SolutionFile} above the test binaries");
    }
}
