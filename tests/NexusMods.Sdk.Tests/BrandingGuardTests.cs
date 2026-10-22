using System.Text.RegularExpressions;
using FluentAssertions;

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
        ("src/NexusMods.App/UpstreamData.cs", "return fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(\"NexusMods.App/DataModel\");", "locates upstream's data folder on purpose, to warn about shared game folders"),
        ("src/NexusMods.App/app.pupnet.conf", "# Reference Config: https://raw.githubusercontent.com/kuiperzone/PupNet-HelloWorld/main/HelloWorld.pupnet.conf", "points at the PupNet tool's sample config, not at upstream"),
        ("CONTRIBUTING.md", "SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App),", "credits the project this is forked from"),
        ("src/NexusMods.App/io.github.CaptSilver.SilverModManager.metainfo.xml", "<p>SilverModManager is a Linux mod manager for your games. It is a fork of the Nexus Mods App and downloads mods from Nexus Mods. It is not affiliated with or endorsed by Nexus Mods.</p>", "non-affiliation statement names the project this is forked from"),
        ("README.md", "SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App) that downloads mods from [Nexus Mods](https://www.nexusmods.com).", "credits the project this is forked from"),
        ("README.md", "It is not affiliated with or endorsed by Nexus Mods. It is a modified version of the Nexus Mods App, which is GPL-3.0-only;", "non-affiliation statement names the project this is forked from"),
        ("src/NexusMods.App/Program.cs", "_logger.LogWarning(\"Upstream Nexus Mods App data found at {Path}; unmanage shared games there first\", UpstreamData.DataModelPath(fileSystem));", "tells the user about the upstream install on purpose"),
        ("src/NexusMods.Games.Bethesda.Loot/Masterlists/CachingMasterlistStore.cs", "public const string Endpoint = \"https://raw.githubusercontent.com/loot/\";", "the LOOT masterlist refresh downloads from LOOT's own repositories, not upstream's"),
        ("src/NexusMods.App/UpstreamData.cs", "/// Detects data left by the upstream Nexus Mods App, which this fork can run next to. The fork", "names the upstream install this class detects"),
    ];

    // Data folder, config folder, log and sync file names that would collide with a running
    // upstream install, upstream's HTTP user agent, upstream's desktop entry and app id, upstream's GitHub organization, raw.githubusercontent.com (nothing is fetched from upstream's repository at runtime), and the Nexus logo assets and their Developer Logo comment, the Premium pictogram, the Premium upsell bullet strings and the toolbar's Premium sample text, the app's English name, links into upstream's repository, docs site, Discord, forums and feedback form (issue and pull request links in comments are provenance and stay). `nexus-logout` is the login verb, not a logo, hence the lookahead.
    [GeneratedRegex("\"NexusMods\\.App[/\"-]|nexusmods\\.app\\.(main|slim)|\"NexusModsApp\"|com\\.nexusmods|nexus_mods_app|StartupWMClass=NexusMods|\"Nexus-Mods\"|raw\\.githubusercontent|nexus-logo(?!ut)|Nexus '?Developer'? Logo|premium\\.svg|UpgradeToPremiumView_Bullet|Increase speed with Premium|Nexus Mods [Aa]pp|Nexus-Mods/NexusMods\\.App(?!/(issues|pull|discussions)/)|nexus-mods\\.github\\.io|discord\\.gg|forums\\.nexusmods|forms\\.gle")]
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

    // The upstream project name used as a binary or package name. Namespaces, project folders,
    // `.csproj`/`.sln` names and avares:// URIs keep the name, so the lookahead drops anything
    // followed by a path or identifier character and the line check drops the declarations that
    // end right after it.
    [GeneratedRegex(@"NexusMods\.App(?![\w./\\])")]
    private static partial Regex UpstreamBinaryName();

    private static readonly string[] DeclarationMarkers =
        ["namespace ", "using ", "x:Class=", "xmlns", "InternalsVisibleTo", "ProjectReference", "avares://"];

    private static readonly (string File, string Line, string Reason)[] AllowedBinaryName =
    [
        ("README.md", "SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App) that downloads mods from [Nexus Mods](https://www.nexusmods.com).", "credits the project this is forked from"),
        ("CONTRIBUTING.md", "SilverModManager is a Linux-only fork of the [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App),", "credits the project this is forked from"),
        ("src/NexusMods.Networking.ModUpdates/PerFeedCacheUpdater.cs", "/// For full info on internals, see the NexusMods.App project documentation.", "points at upstream's documentation, which is where those internals are described"),
    ];

    [Fact]
    public void Test_NoUpstreamBinaryName()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", SourceExtensions)
            .Concat(RootFiles.Where(f => File.Exists(Path.Combine(root, f))));
        var found = RepoScanGuard.FindLines(root, files, line =>
            UpstreamBinaryName().IsMatch(line) && !DeclarationMarkers.Any(line.Contains));

        RepoScanGuard.AssertMatchesAllowlist(found, AllowedBinaryName, "the binary and package are named after the fork; use ApplicationConstants.ProductName or add the line to the allowlist with a reason");
    }

    // The accent is mapped from the neutral Zinc family. The Orange primitives are still
    // defined in PrimitiveColors.axaml, so pointing the accent back at them would restore
    // upstream's orange.
    [GeneratedRegex("BrandPrimary.*PrimitiveOrange")]
    private static partial Regex UpstreamAccent();

    [Fact]
    public void Test_BrandAccentIsNotUpstreamOrange()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var brandColors = "src/NexusMods.Themes.NexusFluentDark/Resources/Palette/Colors/BrandColors.axaml";
        var found = RepoScanGuard.FindLines(root, [brandColors], line => UpstreamAccent().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, [], "BrandPrimary* must map to the Zinc primitives, not Orange");
    }

    // Locale files fall back to English for a missing key, so deleting the translation of a
    // reworded or removed string is what stops the old wording (and the upstream app name in
    // it) from showing up in that language.
    private static readonly string[] RewordedKeys =
    [
        "OpenDiscordMenuItem.Header",
        "OpenForumsMenuItem.Header",
        "Updated_ViewUninstallDocs",
        "OpenStatusPageMenuItem.Header",
        "TopBar_GiveFeedback_ToolTip",
    ];

    [Fact]
    public void Test_RewordedStringsHaveNoStaleTranslations()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var resources = Path.Combine(root, "src/NexusMods.App.UI/Resources");

        var stale = Directory.EnumerateFiles(resources, "Language.*.resx")
            .SelectMany(file => System.Xml.Linq.XDocument.Load(file).Descendants("data")
                .Select(data => (string?)data.Attribute("name"))
                .Where(name => name is not null && RewordedKeys.Contains(name))
                .Select(name => $"{Path.GetFileName(file)}: {name}"))
            .OrderBy(x => x);

        string.Join(Environment.NewLine, stale).Should().BeEmpty("a translation of a reworded string must be deleted so the locale falls back to the new English text");

        var removed = System.Xml.Linq.XDocument.Load(Path.Combine(resources, "Language.resx")).Descendants("data")
            .Select(data => (string?)data.Attribute("name"))
            .Where(name => name is "OpenDiscordMenuItem.Header" or "OpenForumsMenuItem.Header");
        removed.Should().BeEmpty("the Discord and forums menu entries are gone");
    }
}
