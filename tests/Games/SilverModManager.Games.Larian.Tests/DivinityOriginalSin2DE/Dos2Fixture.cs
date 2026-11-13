using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.DivinityOriginalSin2DE;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.Modsettings;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Games.Larian.Tests.BaldursGate3;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>
/// An isolated Divinity: Original Sin 2 Definitive Edition whose Documents folder lives in a throwaway prefix.
/// Player profiles are folders under its <c>PlayerProfiles</c>; mods are installed with paks whose bytes are in
/// the file store, the way an installed mod's are.
/// </summary>
public abstract class Dos2Fixture(ITestOutputHelper helper)
    : AIsolatedGameTest<Dos2Fixture, Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE>(helper)
{
    protected const string SharedUuid = "2bd9bdbe-22ae-4aa2-9c93-205880fc6564";
    protected const string SharedDosUuid = "eedf7638-36ff-4f26-a50a-076b87d53ba0";
    protected const string OriginsUuid = "1301db3d-1f54-4e98-9be5-5094030916e4";
    protected const string GiftBagUuid = "9b45f7e5-d4e2-4fc2-8ef7-3b8e90a5256c";
    protected const string AlphaUuid = "a1a1a1a1-0000-4000-8000-000000000001";
    protected const string BetaUuid = "b2b2b2b2-0000-4000-8000-000000000002";
    protected const string CampaignUuid = "c4c4c4c4-0000-4000-8000-000000000004";

    protected static readonly Dos2ModsettingsSchema Schema = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddDivinityOriginalSin2DE()
        .AddDiagnosticWriter()
        .AddUniversalGameLocator<Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE>(new Version("3.6.117"), locationsFileSystem: IsolatedKnownFolders.Create);

    protected AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    protected static GamePath Pak(string name) => new(LarianConstants.ModsLocationId, name);

    protected static GamePath ModsettingsOf(string profile) => new(LarianConstants.PlayerProfilesLocationId, $"{profile}/modsettings.lsx");

    protected static ModsettingsDocument.ModuleEntry Entry(string uuid, string name, string version = "268435456") =>
        new(Folder: name, Name: name, PublishHandle: "", Uuid: uuid, Version64: version, Md5: "");

    /// <summary>The adventure block and gift bag the game writes, around which the app's paks go.</summary>
    protected static ModsettingsDocument.ModuleEntry[] GameEntries =>
    [
        Entry(SharedUuid, "Shared"), Entry(SharedDosUuid, "Shared_DOS"), Entry(OriginsUuid, "DivinityOrigins"), Entry(GiftBagUuid, "8 Action Points"),
    ];

    /// <summary>A player profile folder holding the mod list the game wrote.</summary>
    protected async Task WriteProfile(string profile, params ModsettingsDocument.ModuleEntry[] entries)
    {
        var file = OnDisk(ModsettingsOf(profile));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(Schema.Render(Schema.DefaultVersion, entries, layoutFrom: null));
    }

    /// <summary>A player profile folder the game made but has not written a mod list into.</summary>
    protected void WriteEmptyProfile(string profile) => OnDisk(new GamePath(LarianConstants.PlayerProfilesLocationId, profile)).CreateDirectory();

    protected void PickProfile(string profile) =>
        ServiceProvider.GetRequiredService<ISettingsManager>().Update<DivinityOriginalSin2DESettings>(s =>
        {
            s.ProfileName = profile;
            return s;
        });

    protected string DiskText(string profile) => File.ReadAllText(OnDisk(ModsettingsOf(profile)).ToString());

    protected ModsettingsDocument Disk(string profile) => Schema.Parse(DiskText(profile));

    protected string[] DiskNames(string profile) => Disk(profile).Entries.Select(e => e.Name).ToArray();

    /// <summary>A pak of a Divinity module, as an LSPK v13 package whose <c>meta.lsx</c> says <c>Version</c> as an int32.</summary>
    protected static byte[] ModulePak(string folder, string name, string uuid, string? type = null, int version = 268435456)
    {
        var meta = SyntheticPak.MetaLsx(folder, name, uuid, (ulong)version, "0", type)
            .Replace("""<attribute id="Version64" type="int64" """, """<attribute id="Version" type="int32" """);
        return SyntheticPak.Build(13, ($"Mods/{folder}/meta.lsx", meta));
    }

    protected static byte[] AlphaPak() => ModulePak("Alpha", "Alpha", AlphaUuid);
    protected static byte[] BetaPak() => ModulePak("Beta", "Beta", BetaUuid);

    /// <summary>Installs a mod holding one pak whose bytes are in the file store.</summary>
    protected async Task<LoadoutItemGroupId> InstallMod(Loadout.ReadOnly loadout, string modName, string pakName, byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(pakName, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), size)]);

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

    protected ILarianPakSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();

    protected SortOrderId SortOrderOf(Loadout.ReadOnly loadout) => Variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
}
