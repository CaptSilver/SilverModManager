using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Games;
using DynamicData.Kernel;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.Larian.BaldursGate3.GameInstalled;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.Modsettings;

/// <summary>
/// An isolated Baldur's Gate 3 with a synthetic <c>modsettings.lsx</c>, for tests that run real syncs and read the
/// file back. Mods are installed with paks whose bytes are in the file store, the way an installed mod's are, and
/// the paks the game downloaded itself are written into the Mods folder of the prefix.
/// </summary>
public abstract class ModsettingsFixture(ITestOutputHelper helper)
    : AIsolatedGameTest<ModsettingsFixture, Larian.BaldursGate3.BaldursGate3>(helper)
{
    protected const string AlphaUuid = "a1a1a1a1-0000-4000-8000-000000000001";
    protected const string BetaUuid = "b2b2b2b2-0000-4000-8000-000000000002";
    protected const string GameModUuid = "c3c3c3c3-0000-4000-8000-000000000003";
    protected const string GameModMd5 = "0123456789abcdef0123456789abcdef";
    protected const string GameModPak = "gamemod-ab12.pak";
    protected const string GustavUuid = "cb555efe-2d9e-131f-8195-a89329d218ea";
    protected const string HonourUuid = "767d0062-d82c-279c-e16b-dfee7fe94cdd";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddBaldursGate3()
        .AddDiagnosticWriter()
        .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.61"), locationsFileSystem: IsolatedKnownFolders.Create);

    protected static readonly GamePath ModsettingsPath = new(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx");

    protected AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    protected static GamePath Pak(string name) => new(Bg3Constants.ModsLocationId, name);

    protected async Task WritePak(string name, byte[] bytes)
    {
        var file = OnDisk(Pak(name));
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
    }

    /// <summary>The game's own two modules, the way a real file lists them.</summary>
    protected static readonly string[] BaseEntries =
    [
        Entry("GustavX", "GustavX", "0", GustavUuid, "145241302737902957"),
        Entry("HonourX", "HonourX", "0", HonourUuid, "36028797025827000"),
    ];

    protected static string Entry(string folder, string name, string handle, string uuid, string version64 = "36028797018963968", string md5 = "") => $"""
        <node id="ModuleShortDesc">
            <attribute id="Folder" type="LSString" value="{folder}"/>
            <attribute id="MD5" type="LSString" value="{md5}"/>
            <attribute id="Name" type="LSString" value="{name}"/>
            <attribute id="PublishHandle" type="uint64" value="{handle}"/>
            <attribute id="UUID" type="guid" value="{uuid}"/>
            <attribute id="Version64" type="int64" value="{version64}"/>
        </node>
        """;

    protected static string GameModEntry() => Entry("GameMod", "Game Mod", "4242", GameModUuid, SyntheticPak.Version(1, 2, 3).ToString(), GameModMd5);

    protected async Task WriteModsettings(string[] entries, string version = """<version major="4" minor="8" revision="0" build="400"/>""")
    {
        var file = OnDisk(ModsettingsPath);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
                {version}
                <region id="ModuleSettings"><node id="root"><children><node id="Mods"><children>
            {string.Concat(entries)}
                </children></node></children></node></region>
            </save>
            """);
    }

    /// <summary>Writes the pak the game downloaded and the entry that names it.</summary>
    protected async Task WriteGameInstalledPak(params string[] otherEntries)
    {
        await WritePak(GameModPak, SyntheticPak.ForPublishedModule("GameMod", "Game Mod", GameModUuid, SyntheticPak.Version(1, 2, 3), "4242"));
        await WriteModsettings([.. BaseEntries, GameModEntry(), .. otherEntries]);
    }

    protected static byte[] AlphaPak() => SyntheticPak.ForModule("Alpha", "Alpha", AlphaUuid, SyntheticPak.Version(1));
    protected static byte[] BetaPak() => SyntheticPak.ForModule("Beta", "Beta", BetaUuid, SyntheticPak.Version(2));

    /// <summary>Installs a mod holding one pak whose bytes are in the file store.</summary>
    protected async Task<LoadoutItemGroupId> InstallMod(Loadout.ReadOnly loadout, string modName, string pakName, byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(pakName, new MemoryStream(bytes)), hash, size)]);

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
                    Name = pakName,
                },
                TargetPath = Pak(pakName).ToGamePathParentTuple(loadout.Id),
            },
            Hash = hash,
            Size = size,
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    protected async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    protected IBg3PakSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();

    protected SortOrderId SortOrderOf(Loadout.ReadOnly loadout) => Variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;

    protected string DiskText() => File.ReadAllText(OnDisk(ModsettingsPath).ToString());

    protected ModsettingsDocument Disk() => ModsettingsDocument.Parse(DiskText());

    protected string[] DiskNames() => Disk().Entries.Select(e => e.Name).ToArray();

    protected Bg3ModListNotice.ReadOnly? Notice(Loadout.ReadOnly loadout) =>
        Bg3ModListNotice.FindBySortOrder(Connection.Db, SortOrderOf(loadout)).Cast<Bg3ModListNotice.ReadOnly?>().FirstOrDefault();

    protected GameInstalledGroup.ReadOnly GameInstalledGroupOf(Loadout.ReadOnly loadout) =>
        GameInstalledGroup.FindByInstalledFor(Connection.Db, loadout.Id).Single();

    protected static string[] Lines(Optional<string> text) => !text.HasValue || text.Value.Length == 0 ? [] : text.Value.Split('\n');
}
