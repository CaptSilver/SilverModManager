using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

public class BareHashApiGuard
{
    [Fact]
    public void FileIdentity_DeclaresNoEqualityOperators()
    {
        // Two identities of different kinds can name one file, so == would answer "different" for the same bytes.
        // Callers spell the question with MatchSameKind (or the resolver) instead.
        var operators = typeof(FileIdentity).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .Where(n => n is "op_Equality" or "op_Inequality");

        operators.Should().BeEmpty();
    }

    private const string BareHashTypeName = "NexusMods.Hashing.xxHash3.Hash";

    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Types that may still expose a bare xxHash3 <c>Hash</c>, each tagged with the area still to be converted or
    /// <c>permanent</c> for the owners of the private store key (NX store, staging names, GC archive map, NX headers).
    /// </summary>
    private static readonly Dictionary<string, string> AllowedTypes = new()
    {
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation+New"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.LocalFileHash+New"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.LocalFileHash+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.DiskStateEntry+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.DiskStateEntry+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameBackedUpFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameBackedUpFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameInstalledFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameInstalledFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.LoadoutIniState+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.LoadoutIniState+ReadOnly"] = "permanent",
        ["SilverModManager.App.GarbageCollection.ArchiveGarbageCollector`2"] = "permanent",
        ["SilverModManager.App.GarbageCollection.ArchiveGarbageCollector`2+RepackDelegate"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Interfaces.IHaveFileHash"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Nx.FileEntryWrapper"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Nx.NxRepacker"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Structs.ArchiveReference`1"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Structs.HashEntry"] = "permanent",
        ["SilverModManager.Deployment.Vfs.VfsCatalogEntry+New"] = "permanent",
        ["SilverModManager.Deployment.Vfs.VfsCatalogEntry+ReadOnly"] = "permanent",
        ["SilverModManager.Games.FileHashes.DTOs.Asset"] = "permanent",
        ["SilverModManager.Sdk.Hashes.FileHashing"] = "permanent",
        ["SilverModManager.Sdk.Hashes.FileIdentity"] = "permanent",
        ["SilverModManager.Sdk.Hashes.HashAttribute"] = "permanent",
        ["SilverModManager.Sdk.Hashes.HashJsonConverter"] = "permanent",
        ["SilverModManager.Sdk.Hashes.MultiHash"] = "permanent",
        ["SilverModManager.Sdk.Hashes.Xx3Hasher"] = "permanent",
        ["SilverModManager.Sdk.Hashes.Xx64Hasher"] = "permanent",
        ["SilverModManager.Sdk.Library.LibraryFile"] = "permanent",
        ["SilverModManager.Sdk.Library.LibraryFile+New"] = "permanent",
        ["SilverModManager.Sdk.Library.LibraryFile+ReadOnly"] = "permanent",
        // Hashes of the schema itself, not of file content.
        ["SilverModManager.DataModel.SchemaVersions.SchemaFingerprint"] = "permanent",
        // The fixture manifests' JSON carries the file's xxHash3 under "Hash" and Hash's own converter reads it.
        ["SilverModManager.Games.TestFramework.Downloader.NexusModMetadata"] = "permanent",
        // These two hash a resource identifier (PersistedDbResource.ResourceIdentifierHash is a HashAttribute), not
        // file content, so there is no FileIdentity to convert them to.
        ["SilverModManager.Sdk.Resources.Extensions"] = "permanent",
        ["SilverModManager.Sdk.Resources.PersistedDbResourceLoader`1+IdentifierToHash"] = "permanent",
        ["SilverModManager.Sdk.Resources.PersistedDbResource+New"] = "permanent",
        ["SilverModManager.Sdk.Resources.PersistedDbResource+ReadOnly"] = "permanent",
    };

    /// <summary>
    /// True when <paramref name="type"/> is, wraps or contains a bare <c>Hash</c>: through generic arguments
    /// (<c>Optional&lt;Hash&gt;</c>, <c>IEnumerable&lt;Hash&gt;</c>, tuples), arrays, pointers and by-ref.
    /// </summary>
    private static bool MentionsBareHash(Type type)
    {
        if (type.FullName == BareHashTypeName)
            return true;
        if (type.HasElementType && type.GetElementType() is { } element)
            return MentionsBareHash(element);
        return type.IsGenericType && type.GetGenericArguments().Any(MentionsBareHash);
    }

    // One rule for every member kind: anything not private, including private protected.
    private static bool IsReachable(MethodBase member) =>
        member.IsPublic || member.IsFamily || member.IsAssembly || member.IsFamilyOrAssembly || member.IsFamilyAndAssembly;

    private static bool IsReachable(FieldInfo field) =>
        field.IsPublic || field.IsFamily || field.IsAssembly || field.IsFamilyOrAssembly || field.IsFamilyAndAssembly;

    private static bool IsCompilerGenerated(MemberInfo member) => member.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    private static bool IsReachableType(Type type) =>
        !IsCompilerGenerated(type) && (type.IsNested ? type.IsNestedPublic || type.IsNestedFamily || type.IsNestedAssembly || type.IsNestedFamORAssem || type.IsNestedFamANDAssem : true);

    private static IEnumerable<string> BareHashMembers(Type type)
    {
        foreach (var method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
        {
            if (!IsReachable(method) || IsCompilerGenerated(method))
                continue;
            var parameters = method.GetParameters();
            var returnType = method is MethodInfo info ? info.ReturnType : typeof(void);
            if (!MentionsBareHash(returnType) && !parameters.Any(p => MentionsBareHash(p.ParameterType)))
                continue;
            var signature = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            yield return $"{type.FullName}.{method.Name}({signature}) : {returnType.Name}";
        }

        foreach (var property in type.GetProperties(AllDeclared))
        {
            var reachable = new[] { property.GetMethod, property.SetMethod }.Any(a => a is not null && IsReachable(a));
            if (!reachable || IsCompilerGenerated(property))
                continue;
            if (MentionsBareHash(property.PropertyType))
                yield return $"{type.FullName}.{property.Name} : {property.PropertyType.Name}";
        }

        foreach (var field in type.GetFields(AllDeclared))
        {
            if (!IsReachable(field) || IsCompilerGenerated(field))
                continue;
            if (MentionsBareHash(field.FieldType))
                yield return $"{type.FullName}.{field.Name} : {field.FieldType.Name}";
        }
    }

    private static string[] OffendingMembers() =>
        PersistedNameGuards.LoadAppAssemblies()
            .SelectMany(PersistedNameGuards.LoadableTypes)
            .Where(IsReachableType)
            .Where(t => t.DeclaringType != typeof(BareHashApiGuard)) // the scanner's own fixtures offend on purpose
            .Where(t => !AllowedTypes.ContainsKey(t.FullName ?? t.Name))
            .SelectMany(BareHashMembers)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void NoFirstPartyApiCarriesABareHashForFileContent()
    {
        // A bare Hash cannot say which digest it is, so a SHA-1 or xxHash64 name for the same bytes never joins it.
        // Public and internal members pass a FileIdentity; the private store key keeps Hash and is listed in AllowedTypes.
        string.Join(Environment.NewLine, OffendingMembers()).Should().BeEmpty();
    }

    [Fact]
    public void AllowListEntriesAllResolveAndStillOffend()
    {
        // A dead entry (renamed or already converted type) would otherwise keep exempting the type, and any bare Hash added to it later.
        var loaded = PersistedNameGuards.LoadAppAssemblies()
            .SelectMany(PersistedNameGuards.LoadableTypes)
            .ToLookup(t => t.FullName ?? t.Name);

        var dead = AllowedTypes.Keys
            .Where(name => !loaded[name].Any(type => BareHashMembers(type).Any()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string.Join(Environment.NewLine, dead).Should().BeEmpty();
    }

    [Fact]
    public void BareHashMembers_ReportsEveryWrappedFormAndSkipsPrivateMembers()
    {
        var members = BareHashMembers(typeof(BareHashFixture)).ToArray();

        string[] expected =
        [
            nameof(BareHashFixture.Direct), nameof(BareHashFixture.Wrapped), nameof(BareHashFixture.Sequence),
            nameof(BareHashFixture.Tuple), nameof(BareHashFixture.Array), nameof(BareHashFixture.ByRef),
            nameof(BareHashFixture.ReturnsHash), nameof(BareHashFixture.ReturnsSequence),
            nameof(BareHashFixture.InternalField), "PrivateProtectedField",
            nameof(BareHashFixture.PrivateGetPublicSet), nameof(BareHashFixture.PrivateGetInternalInit),
            ".ctor",
        ];
        foreach (var name in expected)
            members.Should().Contain(m => m.Contains($".{name}(") || m.Contains($".{name} :"), because: name);

        members.Should().NotContain(m => m.Contains("Hidden"));
    }

    [Fact]
    public void BareHashMembers_ReportsNothingForATypeWithoutBareHashes()
    {
        BareHashMembers(typeof(CleanFixture)).Should().BeEmpty();
    }

    internal sealed class CleanFixture
    {
        public int Number { get; set; }
        public string Text(int value) => value.ToString();
    }

    internal class BareHashFixture
    {
        public BareHashFixture(Hash fromConstructor) { }
        public Hash Direct { get; set; }
        public Optional<Hash> Wrapped { get; set; }
        public IEnumerable<Hash> Sequence { get; set; } = [];
        public (Hash, int) Tuple { get; set; }
        public Hash[] Array { get; set; } = [];
        public void ByRef(ref Hash hash) { }
        public Hash ReturnsHash() => default;
        public IEnumerable<Hash> ReturnsSequence() => [];
        internal Hash InternalField = default;
        private protected Hash PrivateProtectedField = default;
        public Hash PrivateGetPublicSet { private get; set; }
        public Hash PrivateGetInternalInit { private get; init; }
        private Hash HiddenField = default;
        private Hash HiddenProperty { get; set; }
        private void HiddenMethod(Hash hash) { }
    }

    [Fact]
    public void AllowListHasOnlyPermanentEntries()
    {
        AllowedTypes.Where(e => e.Value != "permanent").Select(e => $"{e.Key} ({e.Value})").Should().BeEmpty();
    }
}
