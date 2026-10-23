using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// First-party code, project files, resources and scripts carry the fork's name. A leftover upstream
/// namespace compiles in some places (a using that still resolves through a stale reference) and fails
/// at runtime in others (an avares host, a resx base name, a generated namespace), so the whole tree is
/// scanned for it. The third-party NexusMods.Paths, Hashing, Archives and HyperDuck packages and links
/// to upstream's repository and documentation are not first-party names and do not count.
/// </summary>
public partial class FirstPartyNamespaceGuardTests
{
    private const string HashesContract = "symbols stored in the downloaded game-hashes database, renaming them needs that database rebuilt and re-released first";
    private const string MnemonicContract = "stored by MnemonicDB under this name, renaming it needs a database migration";
    private const string DetectionContract = "matches the install folder of upstream's app, which is not ours to rename";
    private const string PackagePattern = "a pattern for third-party package ids";
    private const string UpstreamDocs = "points at upstream's documentation, which is where those internals are described";
    private const string PrefixText = "names the upstream prefix on purpose: the guards and the notice talk about it";

    private static readonly string[] Patterns =
        ["*.cs", "*.axaml", "*.csproj", "*.props", "*.targets", "*.sql", "*.xml", "*.yaml", "*.yml", "*.sh", "*.sln", "*.DotSettings"];

    private static readonly (string File, string Line, string Reason)[] Allowed =
    [
        (".github/dependabot.yml", "- \"NexusMods.*\"", PackagePattern),
        (".github/dependabot.yml", "- dependency-name: \"NexusMods.*\"", PackagePattern),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/EpicGameStoreBuild.cs", "private const string Namespace = \"NexusMods.Stores.EpicGameStore.EpicGameStoreBuild\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/GogBuild.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogBuild\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/GogDepot.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogDepot\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/GogManifest.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogManifest\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/HashRelation.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.HashRelation\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/PathHashRelation.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.PathHashRelation\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/SteamManifest.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.SteamManifest\";", HashesContract),
        ("src/SilverModManager.Abstractions.Games.FileHashes/Models/VersionDefinition.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.VersionDefinition\";", HashesContract),
        ("src/SilverModManager.App/UpstreamData.cs", "return fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(\"NexusMods.App/DataModel\");", DetectionContract),
        ("src/SilverModManager.MnemonicDB.Abstractions/BuiltInEntities/AttributeDefinition.cs", "private const string Namespace = \"NexusMods.MnemonicDB.DatomStore\";", MnemonicContract),
        ("src/SilverModManager.MnemonicDB.Abstractions/BuiltInEntities/Transaction.cs", "private const string Namespace = \"NexusMods.MnemonicDB.Transaction\";", MnemonicContract),
        ("src/SilverModManager.MnemonicDB/InternalTxFunctions/SimpleMigration.cs", "private static string[] InternalNamespaces = [\"NexusMods.MnemonicDB.DatomStore\", \"NexusMods.MnemonicDB.Transactions\"];", MnemonicContract),
        ("src/SilverModManager.Networking.ModUpdates/PerFeedCacheUpdater.cs", "/// For full info on internals, see the NexusMods.App project documentation.", UpstreamDocs),
        ("src/SilverModManager.Sdk/FirstPartyNames.cs", "public const string UpstreamPersistedPrefix = \"NexusMods.\";", PrefixText),
        ("tests/Games/SilverModManager.Games.FileHashes.Tests/KeptHashDbIdentifierTests.cs", "namespaces.Should().Contain(\"NexusMods.Abstractions.Games.FileHashes.GogBuild\");", HashesContract),
        ("tests/Games/SilverModManager.Games.FileHashes.Tests/KeptHashDbIdentifierTests.cs", "namespaces.Should().Contain(\"NexusMods.Stores.EpicGameStore.EpicGameStoreBuild\");", HashesContract),
        ("tests/SilverModManager.CLI.Tests/UpstreamDataTests.cs", "var dataModel = fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(\"NexusMods.App/DataModel\");", DetectionContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/FirstPartyAssemblyScanTests.cs", "FirstPartyNames.UpstreamPersistedPrefix.Should().Be(\"NexusMods.\");", PrefixText),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.GogBuild\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.GogDepot\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.GogManifest\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.HashRelation\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.PathHashRelation\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.SteamManifest\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Abstractions.Games.FileHashes.VersionDefinition\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.MnemonicDB.DatomStore\", \"stored by MnemonicDB under this name, renaming it needs a database migration\"),", MnemonicContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.MnemonicDB.Transaction\", \"stored by MnemonicDB under this name, renaming it needs a database migration\"),", MnemonicContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "(\"NexusMods.Stores.EpicGameStore.EpicGameStoreBuild\", HashesContract),", HashesContract),
        ("tests/SilverModManager.DataModel.SchemaVersions.Tests/SchemaFailsafes.cs", "/// The only attribute namespaces that keep upstream's `NexusMods.` name.", PrefixText),
        ("tests/SilverModManager.Sdk.Tests/BrandingGuardTests.cs", "(\"src/SilverModManager.App/UpstreamData.cs\", \"return fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(\\\"NexusMods.App/DataModel\\\");\", \"locates upstream's data folder on purpose, to warn about shared game folders\"),", DetectionContract),
        ("tests/SilverModManager.Sdk.Tests/BrandingGuardTests.cs", "(\"src/SilverModManager.Networking.ModUpdates/PerFeedCacheUpdater.cs\", \"/// For full info on internals, see the NexusMods.App project documentation.\", \"points at upstream's documentation, which is where those internals are described\"),", UpstreamDocs),
        ("tests/SilverModManager.Sdk.Tests/NoticeFilesTests.cs", "notice.Should().NotContain(\"NexusMods.* project and namespace names\", \"first-party names carry the fork's name now\");", PrefixText),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/EpicGameStoreBuild.cs\", \"private const string Namespace = \\\"NexusMods.Stores.EpicGameStore.EpicGameStoreBuild\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/GogBuild.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.GogBuild\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/GogDepot.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.GogDepot\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/GogManifest.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.GogManifest\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/HashRelation.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.HashRelation\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/PathHashRelation.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.PathHashRelation\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/SteamManifest.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.SteamManifest\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "(\"src/SilverModManager.Abstractions.Games.FileHashes/Models/VersionDefinition.cs\", \"private const string Namespace = \\\"NexusMods.Abstractions.Games.FileHashes.VersionDefinition\\\";\", HashesContract),", HashesContract),
        ("tests/SilverModManager.Sdk.Tests/PersistedIdentifierGuardTests.cs", "/// fork's name. The only `NexusMods.` literals left are the ones that belong to data the app", PrefixText),
    ];

    /// <summary>
    /// `NexusMods.` at the start of a name, other than behind a letter, digit or underscore (a longer
    /// identifier such as CollectionDownloadNexusMods), behind upstream's repository or documentation
    /// host (which are not paths we own), or in front of a third-party package segment.
    /// </summary>
    [GeneratedRegex(@"(?<![\w])(?<!Nexus-Mods/)(?<!github\.io/)NexusMods\.(?!(Paths|Hashing|Archives|HyperDuck)\b)")]
    private static partial Regex UpstreamPrefix();

    [Fact]
    public void Test_NoUpstreamNamespaceInTrackedSources()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "", includeVendored: true, Patterns)
            .Where(f => !f.EndsWith("/" + nameof(FirstPartyNamespaceGuardTests) + ".cs", StringComparison.Ordinal))
            .ToArray();

        files.Should().Contain(f => f.EndsWith(".sln", StringComparison.Ordinal), "the scan must reach the solution file");
        files.Count(f => f.EndsWith(".csproj", StringComparison.Ordinal)).Should().BeGreaterThan(50, "the scan must reach the project files");
        files.Count(f => f.EndsWith(".cs", StringComparison.Ordinal)).Should().BeGreaterThan(1000, "the scan must reach the sources");

        var found = RepoScanGuard.FindLines(root, files, line => UpstreamPrefix().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, Allowed, "first-party names start with SilverModManager.; add a line to the allowlist with a reason only for a name that belongs to data or software we do not own");
    }

    [Fact]
    public void Test_NoUpstreamNamePrefixInPaths()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var paths = RepoScanGuard.EnumerateFiles(root, "", includeVendored: true, "*")
            .Where(p => p.Split('/').Any(part => UpstreamPrefix().IsMatch(part)))
            .ToArray();

        string.Join(Environment.NewLine, paths).Should().BeEmpty("every project, folder and file carries the SilverModManager. prefix");
    }
}
