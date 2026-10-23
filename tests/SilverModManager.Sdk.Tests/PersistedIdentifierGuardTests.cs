using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace NexusMods.Sdk.Tests;

/// <summary>
/// Strings that end up in the database, in settings file names or in the environment carry the
/// fork's name. The only `NexusMods.` literals left are the ones that belong to data the app
/// does not write itself.
/// </summary>
public partial class PersistedIdentifierGuardTests
{
    private const string HashesContract = "symbols stored in the downloaded game-hashes database, renaming them needs that database rebuilt and re-released first";

    private static readonly (string File, string Line, string Reason)[] AllowedNamespaces =
    [
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/VersionDefinition.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.VersionDefinition\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/EpicGameStoreBuild.cs", "private const string Namespace = \"NexusMods.Stores.EpicGameStore.EpicGameStoreBuild\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/GogBuild.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogBuild\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/SteamManifest.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.SteamManifest\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/GogDepot.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogDepot\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/HashRelation.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.HashRelation\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/GogManifest.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.GogManifest\";", HashesContract),
        ("src/NexusMods.Abstractions.Games.FileHashes/Models/PathHashRelation.cs", "private const string Namespace = \"NexusMods.Abstractions.Games.FileHashes.PathHashRelation\";", HashesContract),
        ("src/NexusMods.App.Generators.Diagnostics/Constants.cs", "public const string Namespace = \"NexusMods.Generators.Diagnostics\";", "names the code generator's C# namespace, not a database id"),
    ];

    [GeneratedRegex("""\bNamespace *(=>|=) *"NexusMods\.|\[JsonName\("NexusMods\.""")]
    private static partial Regex OldPersistedName();

    [Fact]
    public void Test_NoUpstreamNamespaceInPersistedNames()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs");
        var found = RepoScanGuard.FindLines(root, files, line => OldPersistedName().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, AllowedNamespaces, "database namespaces and JSON type names start with SilverModManager.; add the line to the allowlist with a reason only for data the app does not write itself");
    }

    [GeneratedRegex(@"NMA_|\bnma_")]
    private static partial Regex UpstreamPrefix();

    [Fact]
    public void Test_NoUpstreamEnvironmentPrefix()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs", "*.sql");
        var found = RepoScanGuard.FindLines(root, files, line => UpstreamPrefix().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, [], "environment variables use the SMM_ prefix and SQL functions the smm_ prefix");
    }

    [Fact]
    public void Test_EnvironmentVariablesUseSmmPrefix()
    {
        var names = typeof(EnvironmentVariableNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        names.Should().NotBeEmpty();
        names.Should().OnlyContain(n => n.StartsWith("SMM_", StringComparison.Ordinal));
    }
}
