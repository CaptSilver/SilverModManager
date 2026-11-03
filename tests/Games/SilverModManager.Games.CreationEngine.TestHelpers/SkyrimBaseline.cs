using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.TestHelpers;

/// <summary>Makes the stubbed Skyrim SE install know its own Skyrim.esm, the plugin every other one loads after.</summary>
public static class SkyrimBaseline
{
    private const string DataFolder = "Data";

    public static async Task SeedSkyrimEsm(IServiceProvider services, Loadout.ReadOnly loadout)
    {
        var local = ((StubbedFileHasherService)services.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;

        var bytes = SkyrimPluginFile.Bytes("Skyrim.esm");
        var hashes = await MultiHasher.HashStream(new MemoryStream(bytes));
        await services.GetRequiredService<IFileStore>().BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("Skyrim.esm", new MemoryStream(bytes)), FileIdentity.XxHash3(hashes.XxHash3), Size.FromLong(bytes.Length))]);

        using var tx = local.BeginTransaction();
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
        var path = new PathHashRelation.New(tx)
        {
            Path = RelativePath.FromUnsanitizedInput($"{DataFolder}/Skyrim.esm"),
            HashId = relation,
        };
        _ = new LocalBaseline.New(tx)
        {
            Store = install.Store,
            GamePath = install.Path,
            // A baseline counts only for the locator ids it was built for.
            LocatorIds = loadout.LocatorIds.Select(id => id.Value).ToArray(),
            Kind = LocalBaselineKind.Snapshot,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = [path.Id],
        };
        await tx.Commit();
    }
}
