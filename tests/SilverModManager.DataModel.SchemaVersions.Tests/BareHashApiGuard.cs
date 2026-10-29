using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
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
    /// Types that may still expose a bare xxHash3 <c>Hash</c>, each tagged with what removes it: a sweep step or
    /// <c>permanent</c> for the owners of the private store key (NX store, staging names, GC archive map, NX headers).
    /// </summary>
    private static readonly Dictionary<string, string> AllowedTypes = new()
    {
        ["SilverModManager.Abstractions.Collections.Types.HashMapping"] = "14",
        ["SilverModManager.Abstractions.Games.FileHashes.IFileHashesService"] = "14",
        ["SilverModManager.Abstractions.Games.FileHashes.IndexedFileHash"] = "14",
        ["SilverModManager.Abstractions.Games.FileHashes.LocalFileHashRows"] = "14",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation+New"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.HashRelation+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.LocalFileHash+New"] = "permanent",
        ["SilverModManager.Abstractions.Games.FileHashes.Models.LocalFileHash+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Games.Merge.ContestedFile"] = "14",
        ["SilverModManager.Abstractions.GuidedInstallers.OptionImage+ImageStoredFile"] = "14",
        ["SilverModManager.Abstractions.Loadouts.Deployment.ILooseStagingStore"] = "13",
        ["SilverModManager.Abstractions.Loadouts.Deployment.StagingLiveness"] = "13",
        ["SilverModManager.Abstractions.Loadouts.DiskStateEntry+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.DiskStateEntry+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Files.IFileTreeNode"] = "14",
        ["SilverModManager.Abstractions.Loadouts.GameBackedUpFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameBackedUpFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameInstalledFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.GameInstalledFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.LoadoutFile+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment.DeployedFileRows"] = "13",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment.OverridesGroupItems"] = "13",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment.OverridesMovePreview"] = "13",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.GameInstalledPaths"] = "14",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.GameInis"] = "14",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.IniTweakLayer"] = "14",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.IniTweakSource"] = "14",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.LoadoutIniState+New"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.Ini.LoadoutIniState+ReadOnly"] = "permanent",
        ["SilverModManager.Abstractions.Loadouts.Synchronizers.LoadoutSourceItem"] = "14",
        ["SilverModManager.App.GarbageCollection.ArchiveGarbageCollector`2"] = "permanent",
        ["SilverModManager.App.GarbageCollection.ArchiveGarbageCollector`2+RepackDelegate"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Interfaces.IHaveFileHash"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Nx.FileEntryWrapper"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Nx.NxRepacker"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Structs.ArchiveReference`1"] = "permanent",
        ["SilverModManager.App.GarbageCollection.Structs.HashEntry"] = "permanent",
        ["SilverModManager.App.UI.ImagePipelines"] = "14",
        ["SilverModManager.Backend.Games.GameLocationsService"] = "14",
        ["SilverModManager.Backend.Games.StoredHashLookup"] = "14",
        ["SilverModManager.Backend.Games.XxHash3FullFileHasher"] = "14",
        ["SilverModManager.DataModel.GameFileStreamSource"] = "12",
        ["SilverModManager.DataModel.NxFileStore"] = "12",
        ["SilverModManager.DataModel.SchemaVersions.SchemaFingerprint"] = "14",
        ["SilverModManager.DataModel.StreamSourceDispatcher"] = "12",
        ["SilverModManager.Deployment.Links.Backends.HardlinkDeploymentBackend"] = "13",
        ["SilverModManager.Deployment.Links.Backends.LinkDeploymentBackendBase"] = "13",
        ["SilverModManager.Deployment.Links.Backends.ReflinkDeploymentBackend"] = "13",
        ["SilverModManager.Deployment.Links.Backends.SymlinkDeploymentBackend"] = "13",
        ["SilverModManager.Deployment.Staging.LooseStagingStore"] = "13",
        ["SilverModManager.Deployment.Vfs.CatalogBackedHasher"] = "14",
        ["SilverModManager.Deployment.Vfs.IFileHasher"] = "14",
        ["SilverModManager.Deployment.Vfs.IPromotionSink"] = "13",
        ["SilverModManager.Deployment.Vfs.IVfsCatalog"] = "13",
        ["SilverModManager.Deployment.Vfs.JournalEntry"] = "13",
        ["SilverModManager.Deployment.Vfs.LoadoutPromotionSink"] = "13",
        ["SilverModManager.Deployment.Vfs.PromotedFile"] = "13",
        ["SilverModManager.Deployment.Vfs.VfsCatalog"] = "13",
        ["SilverModManager.Deployment.Vfs.VfsCatalogEntry+New"] = "permanent",
        ["SilverModManager.Deployment.Vfs.VfsCatalogEntry+ReadOnly"] = "permanent",
        ["SilverModManager.Deployment.Vfs.VfsFileSource"] = "13",
        ["SilverModManager.Deployment.Vfs.VfsSources"] = "13",
        ["SilverModManager.Deployment.Vfs.XxHash3FileHasher"] = "14",
        ["SilverModManager.Games.FileHashes.DTOs.Asset"] = "permanent",
        ["SilverModManager.Games.FileHashes.FileHashesService"] = "14",
        ["SilverModManager.Games.FromSoft.Heal.RegulationBaselines"] = "14",
        ["SilverModManager.Games.FromSoft.Installers.Me3InstallerFiles+ArchiveFile"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.ArchiveRegulation"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.ArchiveVanillas"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.IVouchedRegulations"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.ProviderFile"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.UnconfirmedDump"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.VanillaDump"] = "14",
        ["SilverModManager.Games.FromSoft.Merge.VouchedRegulations"] = "14",
        ["SilverModManager.Games.Larian.BaldursGate3.Pipelines"] = "14",
        ["SilverModManager.Games.TestFramework.AGameTest`1"] = "14",
        ["SilverModManager.Games.TestFramework.AIsolatedGameTest`2"] = "14",
        ["SilverModManager.Games.TestFramework.ALibraryArchiveInstallerTests`2"] = "14",
        ["SilverModManager.Games.TestFramework.Downloader.NexusModMetadata"] = "14",
        ["SilverModManager.Library.LibraryFileDigests"] = "14",
        ["SilverModManager.Sdk.FileStore.ArchivedFileEntry"] = "12",
        ["SilverModManager.Sdk.FileStore.FileStorePins"] = "12",
        ["SilverModManager.Sdk.FileStore.IFileStore"] = "12",
        ["SilverModManager.Sdk.FileStore.IReadOnlyStreamSource"] = "12",
        ["SilverModManager.Sdk.FileStore.IStreamSourceDispatcher"] = "12",
        ["SilverModManager.Sdk.FileStore.MissingArchiveException"] = "12",
        ["SilverModManager.Sdk.Hashes.FileIdentity"] = "permanent",
        ["SilverModManager.Sdk.Hashes.HashAttribute"] = "permanent",
        ["SilverModManager.Sdk.Hashes.HashJsonConverter"] = "permanent",
        ["SilverModManager.Sdk.Hashes.IFullFileHasher"] = "14",
        ["SilverModManager.Sdk.Hashes.MultiHash"] = "permanent",
        ["SilverModManager.Sdk.Hashes.MultiHashFileHasher"] = "14",
        ["SilverModManager.Sdk.Hashes.Xx3Hasher"] = "permanent",
        ["SilverModManager.Sdk.Hashes.Xx64Hasher"] = "permanent",
        ["SilverModManager.Sdk.IO.SourceDispatcherStreamFactory"] = "12",
        ["SilverModManager.Sdk.Library.LibraryFile"] = "permanent",
        ["SilverModManager.Sdk.Library.LibraryFile+New"] = "permanent",
        ["SilverModManager.Sdk.Library.LibraryFile+ReadOnly"] = "permanent",
        ["SilverModManager.Sdk.Resources.Extensions"] = "12",
        ["SilverModManager.Sdk.Resources.FileStoreLoader"] = "12",
        ["SilverModManager.Sdk.Resources.FileStoreStreamLoader"] = "12",
        ["SilverModManager.Sdk.Resources.PersistedDbResourceLoader`1+IdentifierToHash"] = "12",
        ["SilverModManager.Sdk.Resources.PersistedDbResource+New"] = "permanent",
        ["SilverModManager.Sdk.Resources.PersistedDbResource+ReadOnly"] = "permanent",
        ["SilverModManager.StandardGameLocators.TestHelpers.StubbedFileHasherService"] = "14",
        ["SilverModManager.Wabbajack.Acquire.ArchivePlan"] = "14",
        ["SilverModManager.Wabbajack.Acquire.GameFileSourceLookup"] = "14",
        ["SilverModManager.Wabbajack.Acquire.ResolvedMember"] = "14",
        ["SilverModManager.Wabbajack.Acquire.StreamDigests"] = "14",
        ["SilverModManager.Wabbajack.Acquire.UnmatchedInbox"] = "14",
        ["SilverModManager.Wabbajack.Format.ArchiveEntry"] = "14",
        ["SilverModManager.Wabbajack.Format.ArchiveHashPath"] = "14",
        ["SilverModManager.Wabbajack.Format.ArchiveMeta"] = "14",
        ["SilverModManager.Wabbajack.Format.CreateBsa"] = "14",
        ["SilverModManager.Wabbajack.Format.Directive"] = "14",
        ["SilverModManager.Wabbajack.Format.FromArchive"] = "14",
        ["SilverModManager.Wabbajack.Format.GameFileSourceState"] = "14",
        ["SilverModManager.Wabbajack.Format.InlineFile"] = "14",
        ["SilverModManager.Wabbajack.Format.JsonObject"] = "14",
        ["SilverModManager.Wabbajack.Format.MergedPatch"] = "14",
        ["SilverModManager.Wabbajack.Format.MergedPatchSource"] = "14",
        ["SilverModManager.Wabbajack.Format.PatchedFromArchive"] = "14",
        ["SilverModManager.Wabbajack.Format.PropertyFile"] = "14",
        ["SilverModManager.Wabbajack.Format.RemappedInlineFile"] = "14",
        ["SilverModManager.Wabbajack.Format.TransformedTexture"] = "14",
        ["SilverModManager.Wabbajack.Format.WabbajackHash"] = "14",
        ["SilverModManager.Wabbajack.Gallery.GalleryEntry"] = "14",
        ["SilverModManager.Wabbajack.Install.BuiltArchiveCheck"] = "14",
        ["SilverModManager.Wabbajack.Install.GeneratedFile"] = "14",
        ["SilverModManager.Wabbajack.Install.Xx64HashingStream"] = "14",
        ["SilverModManager.Wabbajack.Library.ILibraryArchiveLocator"] = "14",
        ["SilverModManager.Wabbajack.Library.LibraryArchiveLocator"] = "14",
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

    private static bool IsReachable(MethodBase member) => member.IsPublic || member.IsFamily || member.IsAssembly || member.IsFamilyOrAssembly;

    private static bool IsCompilerGenerated(MemberInfo member) => member.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    private static bool IsReachableType(Type type) =>
        !IsCompilerGenerated(type) && (type.IsNested ? type.IsNestedPublic || type.IsNestedFamily || type.IsNestedAssembly || type.IsNestedFamORAssem : true);

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
            var accessor = property.GetMethod ?? property.SetMethod;
            if (accessor is null || !IsReachable(accessor) || IsCompilerGenerated(property))
                continue;
            if (MentionsBareHash(property.PropertyType))
                yield return $"{type.FullName}.{property.Name} : {property.PropertyType.Name}";
        }

        foreach (var field in type.GetFields(AllDeclared))
        {
            if (field.IsPrivate || IsCompilerGenerated(field))
                continue;
            if (MentionsBareHash(field.FieldType))
                yield return $"{type.FullName}.{field.Name} : {field.FieldType.Name}";
        }
    }

    private static string[] OffendingMembers() =>
        PersistedNameGuards.LoadAppAssemblies()
            .SelectMany(PersistedNameGuards.LoadableTypes)
            .Where(IsReachableType)
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

    [Fact(Skip = "until the sweep lands")]
    public void AllowListHasOnlyPermanentEntries()
    {
        AllowedTypes.Where(e => e.Value != "permanent").Select(e => $"{e.Key} ({e.Value})").Should().BeEmpty();
    }
}
