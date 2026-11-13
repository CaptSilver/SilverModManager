using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.DivinityOriginalSinEE;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Tests.BaldursGate3;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSinEE;

/// <summary>
/// An isolated Divinity: Original Sin Enhanced Edition whose Documents folder lives in a throwaway prefix. Its paks
/// are LSPK v9 packages whose bytes are in the file store, the way an installed mod's are.
/// </summary>
public abstract class DosEeFixture(ITestOutputHelper helper)
    : AIsolatedGameTest<DosEeFixture, Larian.DivinityOriginalSinEE.DivinityOriginalSinEE>(helper)
{
    protected const string AlphaUuid = "a1a1a1a1-0000-4000-8000-000000000001";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddDivinityOriginalSinEE()
        .AddDiagnosticWriter()
        .AddUniversalGameLocator<Larian.DivinityOriginalSinEE.DivinityOriginalSinEE>(new Version("3.6.54"), locationsFileSystem: IsolatedKnownFolders.Create);

    protected AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    protected static GamePath Pak(string name) => new(LarianConstants.ModsLocationId, name);

    protected static byte[] V9Pak(string folder, string name, string uuid) =>
        SyntheticPak.Build(9, ($"Mods/{folder}/meta.lsx", SyntheticPak.MetaLsx(folder, name, uuid, 268435456UL)));

    /// <summary>Installs a mod holding one file whose bytes are in the file store, at <paramref name="target"/>.</summary>
    protected async Task<LoadoutItemGroupId> InstallMod(Loadout.ReadOnly loadout, string modName, GamePath target, byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(target.FileName.ToString(), new MemoryStream(bytes)), FileIdentity.XxHash3(hash), size)]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, modName);
        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    LoadoutId = loadout.Id,
                    ParentId = group,
                    Name = target.FileName.ToString(),
                },
                TargetPath = target.ToGamePathParentTuple(loadout.Id),
            },
            Hash = hash,
            Size = size,
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    protected Task<LoadoutItemGroupId> InstallPak(Loadout.ReadOnly loadout, string pakName, byte[] bytes) =>
        InstallMod(loadout, pakName, Pak(pakName), bytes);

    protected async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));
}
