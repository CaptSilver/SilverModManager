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
        ("src/NexusMods.App/Program.cs", "_logger.LogWarning(\"Upstream Nexus Mods App data found at {Path}; unmanage shared games there first\", UpstreamData.DataModelPath(fileSystem));", "tells the user about the upstream install on purpose"),
        ("src/NexusMods.App/UpstreamData.cs", "/// Detects data left by the upstream Nexus Mods App, which this fork can run next to. The fork", "names the upstream install this class detects"),
        ("README.md", "<h1>The Nexus Mods app</h1>", "README text is upstream's"),
        ("README.md", "<a href=\"https://github.com/Nexus-Mods/NexusMods.App/actions/workflows/clean_environment_tests.yaml\" target=\"_blank\"><img src=\"https://github.com/Nexus-Mods/NexusMods.App/actions/workflows/clean_environment_tests.yaml/badge.svg\" alt=\"CI Tests\"></a>", "README text is upstream's"),
        ("README.md", "<a href=\"https://discord.gg/ReWTxb93jS\" target=\"_blank\"><img src=\"https://img.shields.io/discord/1134149061080002713?logo=discord&logoColor=white&color=7289da\" alt=\"Discord\"></a>", "README text is upstream's"),
        ("README.md", "Nexus Mods app is a mod installer, creator and manager for all your popular games.", "README text is upstream's"),
        ("README.md", "Learn more about the App on the [Wiki](https://nexus-mods.github.io/NexusMods.App/)", "README text is upstream's"),
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
