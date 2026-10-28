using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// Writes local baseline rows into a hash store. Shared by compiling the file into the FileHashes tests, which do not
/// reference this project, so <c>HashAll</c> and <c>AddPaths</c> stay internal for them.
/// </summary>
public static class LocalBaselineRows
{
    /// <summary>
    /// A baseline of the installation at <paramref name="gamePath"/> holding <paramref name="files"/> as vanilla,
    /// with their hash rows, and <paramref name="nonVanilla"/> paths it found modified. Everything is in <paramref name="store"/>.
    /// </summary>
    public static async Task<EntityId> AddLocalBaseline(IConnection store, GameStore gameStore, string gamePath, LocalBaselineKind kind,
        (string Path, string Content)[] files, params string[] nonVanilla)
    {
        var hashes = await HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = AddPaths(tx, hashes);
        var baseline = new LocalBaseline.New(tx)
        {
            Store = gameStore,
            GamePath = gamePath,
            LocatorIds = [],
            Kind = kind,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = pathIds,
        };
        foreach (var path in nonVanilla)
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = baseline.Id,
                Path = RelativePath.FromUnsanitizedInput(path),
                Reason = NonVanillaReason.HashDiffers,
                Size = Size.From(1),
            };
        }
        var result = await tx.Commit();
        return result[baseline.Id];
    }

    internal static async Task<(string Path, MultiHash Hash)[]> HashAll((string Path, string Content)[] files)
    {
        var rows = new List<(string, MultiHash)>();
        foreach (var (path, content) in files)
            rows.Add((path, await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)))));
        return rows.ToArray();
    }

    internal static List<EntityId> AddPaths(ITransaction tx, (string Path, MultiHash Hash)[] files)
    {
        var pathIds = new List<EntityId>();
        foreach (var (path, hashes) in files)
        {
            var relation = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            var pathRelation = new PathHashRelation.New(tx)
            {
                Path = RelativePath.FromUnsanitizedInput(path),
                HashId = relation,
            };
            pathIds.Add(pathRelation.Id);
        }

        return pathIds;
    }
}
