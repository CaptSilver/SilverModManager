using System.Text.RegularExpressions;

namespace NexusMods.Sdk.Tests;

/// <summary>
/// The fork keeps its own state and identity, so upstream's names must not leak back into
/// source, project files, desktop entries or the top-level docs. Lines that name upstream
/// on purpose (provenance, attribution) go in the allowlist with the reason.
/// </summary>
public partial class BrandingGuardTests
{
    private static readonly string[] SourceExtensions = ["*.cs", "*.axaml", "*.resx", "*.csproj", "*.conf", "*.desktop", "*.xml"];
    private static readonly string[] RootFiles = ["README.md", "CONTRIBUTING.md", "NuGet.Build.props"];

    private static readonly (string File, string Line, string Reason)[] Allowed =
    [
        ("src/NexusMods.App.UI/Overlays/Updater/UpdateChecker.cs", "private const string GitHubRepository = \"NexusMods.App\";", "update check still points at upstream's releases until it is gated and repointed"),
        ("src/NexusMods.App/UpstreamData.cs", "return fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(\"NexusMods.App/DataModel\");", "locates upstream's data folder on purpose, to warn about shared game folders"),
    ];

    // Data folder, config folder, log and sync file names that would collide with a running
    // upstream install, upstream's HTTP user agent, and upstream's desktop entry and app id.
    [GeneratedRegex("\"NexusMods\\.App[/\"-]|nexusmods\\.app\\.(main|slim)|\"NexusModsApp\"|com\\.nexusmods|nexus_mods_app|StartupWMClass=NexusMods")]
    private static partial Regex UpstreamIdentity();

    [Fact]
    public void Test_NoUpstreamBrandingInSource()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", SourceExtensions)
            .Concat(RootFiles.Where(f => File.Exists(Path.Combine(root, f))));
        var found = RepoScanGuard.FindLines(root, files, line => UpstreamIdentity().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, Allowed, "the fork must not reuse upstream's names; use ApplicationConstants or add the line to the allowlist with a reason");
    }
}
