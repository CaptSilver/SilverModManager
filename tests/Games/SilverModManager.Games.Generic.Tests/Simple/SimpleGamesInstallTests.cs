using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

/// <summary>
/// One archive per way a table game's mods are laid out: it installs into the game's mod folder with the expected
/// paths, and a layout the game cannot use is refused. Adding a row to <see cref="SimpleGames.All"/> means adding
/// its cases here.
/// </summary>
public class SimpleGamesInstallTests(ITestOutputHelper outputHelper)
    : ALibraryArchiveInstallerTests<SimpleGamesInstallTests>(outputHelper, SimpleGames.Witcher1.GameId)
{
    // The installers under test belong to the row, not to the booted game, which only supplies a loadout to install into.
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddSimpleGames()
        .AddUniversalGameLocator(SimpleGames.Witcher1.GameId, locationsFileSystem: IsolatedKnownFolders.Create);

    // These rows have their own installer tests.
    private static readonly string[] CoveredElsewhere = ["Witcher.Witcher1", "Witcher.Witcher2"];

    public static readonly (string GameId, string[] ArchivePaths, string[] Expected)[] Cases =
    [
        // A folder name the game knows marks the root, however deep; wrappers above it and files beside it are handled.
        ("Simple.DarkestDungeon", ["dungeons/d.bin", "readme.txt"], ["{Game}/dungeons/d.bin", "{Game}/readme.txt"]),
        ("Simple.DarkestDungeon", ["Mod/Heroes/h.bin", "Mod/project.xml"], ["{Game}/Heroes/h.bin", "{Game}/project.xml"]),
        ("Simple.VampireBloodlines", ["maps/a.bsp", "readme.txt"], ["{Game}/vampire/maps/a.bsp", "{Game}/vampire/readme.txt"]),
        ("Simple.VampireBloodlines", ["Mod/cfg/a.cfg", "Mod/Python/b.py"], ["{Game}/vampire/Python/b.py", "{Game}/vampire/cfg/a.cfg"]),
        ("Simple.BatmanArkhamCity", ["CookedPCConsole/a.upk"], ["{Game}/BmGame/CookedPCConsole/a.upk"]),
        ("Simple.BatmanArkhamCity", ["Mod/Config/UserEngine.ini", "Mod/readme.txt"], ["{Game}/BmGame/Config/UserEngine.ini", "{Game}/BmGame/readme.txt"]),
        // Files go under the www folder below any wrapper folder the repository archive adds; its other files are not mod files.
        ("Simple.KarrynsPrison", ["www/js/plugins/a.js"], ["{Game}/www/js/plugins/a.js"]),
        ("Simple.KarrynsPrison", ["repo-main/www/img/a.png", "repo-main/meta.ini"], ["{Game}/www/img/a.png"]),
        // The folder holding the first file of the kind is the root.
        ("Simple.Elex", ["a.pak"], ["{Game}/data/packed/a.pak"]),
        ("Simple.Elex", ["Mod/a.pak", "Mod/readme.txt"], ["{Game}/data/packed/a.pak", "{Game}/data/packed/readme.txt"]),
        ("Simple.TeamFortress2", ["Pack/a.vpk", "Pack/materials/b.vmt"], ["{Game}/tf/custom/a.vpk", "{Game}/tf/custom/materials/b.vmt"]),
        // Paths below a datalocal folder lose it; an archive without one goes in unchanged.
        ("Simple.GreedFall", ["datalocal/a.spk"], ["{Game}/datalocal/a.spk"]),
        ("Simple.GreedFall", ["Mod/b.spk"], ["{Game}/datalocal/Mod/b.spk"]),
        ("Simple.DungeonSiege", ["Resources/a.dsres"], ["{Game}/Resources/a.dsres"]),
        ("Simple.DungeonSiege", ["Maps/m.dsmap"], ["{Game}/Maps/m.dsmap"]),
        ("Simple.DungeonSiege", ["a.dsres"], ["{Game}/Resources/a.dsres"]),
        ("Simple.DungeonSiege", ["m.dsmap"], ["{Game}/Maps/m.dsmap"]),
        ("Simple.DungeonSiege2", ["Resources/a.ds2res"], ["{Game}/Resources/a.ds2res"]),
        ("Simple.DungeonSiege2", ["Maps/m.ds2map"], ["{Game}/Maps/m.ds2map"]),
        ("Simple.DungeonSiege2", ["a.ds2res"], ["{Game}/Resources/a.ds2res"]),
        ("Simple.DungeonSiege2", ["m.ds2map"], ["{Game}/Maps/m.ds2map"]),
        ("Simple.DarkSouls", ["Mod/data.bin", "readme.txt"], ["{Game}/DATA/Mod/data.bin", "{Game}/DATA/readme.txt"]),
        ("Simple.DarkSoulsRemastered", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.DarkSouls2Sotfs", ["Mod/data.bin", "readme.txt"], ["{Game}/Game/Mod/data.bin", "{Game}/Game/readme.txt"]),
        ("Simple.DragonsDogmaDarkArisen", ["Mod/data.bin", "readme.txt"], ["{Game}/nativePC/Mod/data.bin", "{Game}/nativePC/readme.txt"]),
        ("Simple.DarkMessiah", ["Mod/data.bin", "readme.txt"], ["{Game}/mm/Mod/data.bin", "{Game}/mm/readme.txt"]),
        ("Simple.X4Foundations", ["Mod/data.bin", "readme.txt"], ["{Game}/extensions/Mod/data.bin", "{Game}/extensions/readme.txt"]),
        ("Simple.KerbalSpaceProgram", ["Mod/data.bin", "readme.txt"], ["{Game}/GameData/Mod/data.bin", "{Game}/GameData/readme.txt"]),
        ("Simple.Control", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.AssettoCorsa", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.F123", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MetalGearSolid2MasterCollection", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MetalGearSolid3MasterCollection", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MirrorsEdge", ["Mod/data.bin", "readme.txt"], ["{Game}/TdGame/Mod/data.bin", "{Game}/TdGame/readme.txt"]),
        ("Simple.NierAutomata", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.TestDriveUnlimited2", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.TrackmaniaUnitedForever", ["Mod/data.bin", "readme.txt"], ["{Game}/GameData/Mod/data.bin", "{Game}/GameData/readme.txt"]),
        ("Simple.TrainSimulator", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.ValkyriaChronicles", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.BreakingWheel", ["Mod/data.bin", "readme.txt"], ["{Game}/ModdingTools/Mod/data.bin", "{Game}/ModdingTools/readme.txt"]),
        ("Simple.GrimDawn", ["Mod/data.bin", "readme.txt"], ["{Game}/mods/Mod/data.bin", "{Game}/mods/readme.txt"]),
        ("Simple.StateOfDecay", ["Mod/data.bin", "readme.txt"], ["{Game}/game/Mod/data.bin", "{Game}/game/readme.txt"]),
        ("Simple.Starbound", ["Mod/data.bin", "readme.txt"], ["{Game}/mods/Mod/data.bin", "{Game}/mods/readme.txt"]),
        // Mods in a Documents folder go below the location the row maps, which already ends at the mod folder.
        ("Simple.LegendOfGrimrock", ["Mod/data.bin", "readme.txt"], ["{Simple.Documents}/Mod/data.bin", "{Simple.Documents}/readme.txt"]),
        ("Simple.BindingOfIsaacRebirth", ["Mod/data.bin", "readme.txt"], ["{Simple.Documents}/Mod/data.bin", "{Simple.Documents}/readme.txt"]),
        ("Simple.ShadowrunReturns", ["Mod/data.bin", "readme.txt"],
            ["{Game}/Shadowrun_Data/StreamingAssets/ContentPacks/Mod/data.bin", "{Game}/Shadowrun_Data/StreamingAssets/ContentPacks/readme.txt"]),
        // A folder the game knows marks the root, as Vortex's stop patterns do; an archive without one goes in unchanged.
        ("Simple.WolcenLordsOfMayhem", ["levels/a.cry", "readme.txt"], ["{Game}/Game/levels/a.cry", "{Game}/Game/readme.txt"]),
        ("Simple.WolcenLordsOfMayhem", ["Mod/Objects/b.cgf", "Mod/Lib/c.xml"], ["{Game}/Game/Lib/c.xml", "{Game}/Game/Objects/b.cgf"]),
        ("Simple.WolcenLordsOfMayhem", ["Mod/data.bin"], ["{Game}/Game/Mod/data.bin"]),
        // Unity Mod Manager mods have an Info.json and get a folder of their own; anything else goes in unchanged.
        ("Simple.GardenPaws", ["Mod/data.bin"], ["{Game}/Mods/Mod/data.bin"]),
        ("Simple.OxygenNotIncluded", ["Mod/data.bin"], ["{Game}/Mods/Mod/data.bin"]),
        ("Simple.PathfinderKingmaker", ["Mod/data.bin"], ["{Game}/Mods/Mod/data.bin"]),
        ("Simple.PathfinderWrathOfTheRighteous", ["Mod/data.bin"], ["{Game}/Mods/Mod/data.bin"]),
        // A folder of the game's own marks a root mod, wrapper folders above it are dropped; anything else goes in Override.
        ("Simple.KnightsOfTheOldRepublic", ["Override/a.tga", "readme.txt"], ["{Game}/Override/a.tga"]),
        ("Simple.KnightsOfTheOldRepublic", ["Wrap/StreamMusic/x.wav", "Wrap/modules/m.rim"], ["{Game}/StreamMusic/x.wav", "{Game}/modules/m.rim"]),
        ("Simple.KnightsOfTheOldRepublic", ["a.tga", "b.2da"], ["{Game}/override/a.tga", "{Game}/override/b.2da"]),
        ("Simple.KnightsOfTheOldRepublic", ["Mod/a.tga"], ["{Game}/override/Mod/a.tga"]),
        ("Simple.KnightsOfTheOldRepublic2", ["Override/a.tga", "readme.txt"], ["{Game}/Override/a.tga"]),
        ("Simple.KnightsOfTheOldRepublic2", ["Wrap/StreamMusic/x.wav", "Wrap/modules/m.rim"], ["{Game}/StreamMusic/x.wav", "{Game}/modules/m.rim"]),
        ("Simple.KnightsOfTheOldRepublic2", ["a.tga", "b.2da"], ["{Game}/override/a.tga", "{Game}/override/b.2da"]),
        ("Simple.KnightsOfTheOldRepublic2", ["Mod/a.tga"], ["{Game}/override/Mod/a.tga"]),
    ];

    /// <summary>
    /// Archives a game turns away on purpose, with the text the player reads. Each is a plain refusal, not a layout
    /// that merely fails to match, so it must name what the mod needs.
    /// </summary>
    public static readonly (string GameId, string[] ArchivePaths, string MessagePart)[] Refusals =
    [
        // A TSLPatcher mod patches the player's files with its own program; the app only copies files.
        ("Simple.KnightsOfTheOldRepublic", ["tslpatchdata/changes.ini", "TSLPatcher.exe"], "TSLPatcher"),
        ("Simple.KnightsOfTheOldRepublic", ["Mod/tslpatchdata/changes.ini", "Mod/Override/a.tga"], "TSLPatcher"),
        ("Simple.KnightsOfTheOldRepublic", ["Tools/tslpatcher.exe"], "TSLPatcher"),
        ("Simple.KnightsOfTheOldRepublic2", ["tslpatchdata/changes.ini", "TSLPatcher.exe"], "TSLPatcher"),
        ("Simple.KnightsOfTheOldRepublic2", ["Mod/tslpatchdata/changes.ini", "Mod/Override/a.tga"], "TSLPatcher"),
        ("Simple.KnightsOfTheOldRepublic2", ["Tools/tslpatcher.exe"], "TSLPatcher"),
    ];

    /// <summary>Layouts the games with a published layout cannot use.</summary>
    public static readonly (string GameId, string[] ArchivePaths)[] Refused =
    [
        ("Simple.DungeonSiege", ["readme.txt"]),
        ("Simple.DungeonSiege", ["Textures/a.dds"]),
        ("Simple.DungeonSiege2", ["readme.txt"]),
        ("Simple.DungeonSiege2", ["Mod/readme.txt"]),
        ("Simple.DarkestDungeon", ["Mod/data.bin", "readme.txt"]),
        ("Simple.VampireBloodlines", ["Mod/data.bin", "readme.txt"]),
        ("Simple.BatmanArkhamCity", ["Mod/data.bin", "readme.txt"]),
        ("Simple.KarrynsPrison", ["Mod/data.bin", "readme.txt"]),
        ("Simple.AHatInTime", ["Mod/data.bin", "readme.txt"]),
        // A scene mod goes in the Documents folder, which this row does not map.
        ("Simple.DawnOfMan", ["Scenes/a.scn.xml"]),
        ("Simple.DawnOfMan", ["Mod/data.bin", "readme.txt"]),
        ("Simple.Elex", ["Mod/data.bin", "readme.txt"]),
        ("Simple.TeamFortress2", ["Mod/data.bin", "readme.txt"]),
        // Surviving Mars loads a folder holding a modcontent.hpk.
        ("Simple.SurvivingMars", ["Mod/data.bin", "readme.txt"]),
    ];

    /// <summary>Archives whose mod goes in a folder named after the folder holding the marker file, or after the archive.</summary>
    public static readonly (string GameId, string ArchiveFileName, string[] ArchivePaths, string[] Expected)[] NamedCases =
    [
        ("Simple.AHatInTime", "CoolHat.zip", ["modinfo.ini", "Content/a.upk"],
            ["{Game}/HatInTimeGame/Mods/CoolHat/Content/a.upk", "{Game}/HatInTimeGame/Mods/CoolHat/modinfo.ini"]),
        ("Simple.AHatInTime", "download.zip", ["MyHat/modinfo.ini", "MyHat/Content/a.upk", "readme.txt"],
            ["{Game}/HatInTimeGame/Mods/MyHat/Content/a.upk", "{Game}/HatInTimeGame/Mods/MyHat/modinfo.ini"]),
        ("Simple.DawnOfMan", "SomeMod.zip", ["Info.json", "Mod.dll"], ["{Game}/Mods/SomeMod/Info.json", "{Game}/Mods/SomeMod/Mod.dll"]),
        ("Simple.DawnOfMan", "download.zip", ["Wrapper/Info.json", "Wrapper/Mod.dll"], ["{Game}/Mods/Wrapper/Info.json", "{Game}/Mods/Wrapper/Mod.dll"]),
        ("Simple.GardenPaws", "CoolMod.zip", ["Info.json", "Mod.dll"], ["{Game}/Mods/CoolMod/Info.json", "{Game}/Mods/CoolMod/Mod.dll"]),
        ("Simple.OxygenNotIncluded", "CoolMod.zip", ["Info.json", "Mod.dll"], ["{Game}/Mods/CoolMod/Info.json", "{Game}/Mods/CoolMod/Mod.dll"]),
        ("Simple.PathfinderKingmaker", "download.zip", ["Wrapper/Info.json", "Wrapper/Mod.dll"], ["{Game}/Mods/Wrapper/Info.json", "{Game}/Mods/Wrapper/Mod.dll"]),
        ("Simple.PathfinderWrathOfTheRighteous", "CoolMod.zip", ["Info.json", "Mod.dll"], ["{Game}/Mods/CoolMod/Info.json", "{Game}/Mods/CoolMod/Mod.dll"]),
        ("Simple.SurvivingMars", "CoolMod.zip", ["modcontent.hpk", "Code/a.lua"],
            ["{Simple.RoamingAppData}/CoolMod/Code/a.lua", "{Simple.RoamingAppData}/CoolMod/modcontent.hpk"]),
        ("Simple.SurvivingMars", "download.zip", ["MyMod/modcontent.hpk", "MyMod/Code/a.lua"],
            ["{Simple.RoamingAppData}/MyMod/Code/a.lua", "{Simple.RoamingAppData}/MyMod/modcontent.hpk"]),
    ];

    public static IEnumerable<object[]> CaseData() => Cases.Select(row => (object[])[row.GameId, row.ArchivePaths, row.Expected]);

    public static IEnumerable<object[]> NamedCaseData() => NamedCases.Select(row => (object[])[row.GameId, row.ArchiveFileName, row.ArchivePaths, row.Expected]);

    public static IEnumerable<object[]> RefusalData() => Refusals.Select(row => (object[])[row.GameId, row.ArchivePaths, row.MessagePart]);

    public static IEnumerable<object[]> RefusedData() => Refused.Select(row => (object[])[row.GameId, row.ArchivePaths]);

    [Theory]
    [MemberData(nameof(CaseData))]
    public async Task AModArchive_InstallsIntoTheGamesModFolder(string gameId, string[] archivePaths, string[] expected)
    {
        var targets = await Install(RowOf(gameId), archivePaths);

        targets.Should().NotBeNull(because: $"{gameId} should take {string.Join(", ", archivePaths)}");
        targets!.Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(NamedCaseData))]
    public async Task AModArchive_WithAMarkerFile_InstallsIntoAFolderOfItsOwn(string gameId, string archiveFileName, string[] archivePaths, string[] expected)
    {
        var targets = await Install(RowOf(gameId), archivePaths, archiveFileName);

        targets.Should().NotBeNull(because: $"{gameId} should take {string.Join(", ", archivePaths)}");
        targets!.Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(RefusedData))]
    public async Task AWrongLayout_IsNotSupported(string gameId, string[] archivePaths)
    {
        (await Install(RowOf(gameId), archivePaths)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(RefusalData))]
    public async Task AnArchiveTheGameRefuses_FailsWithAMessageForThePlayer(string gameId, string[] archivePaths, string messagePart)
    {
        var act = async () => await Install(RowOf(gameId), archivePaths);

        (await act.Should().ThrowAsync<UserFacingException>()).Which.Message.Should().Contain(messagePart);
    }

    [Fact]
    public void EveryRow_HasAnInstallCase()
    {
        var covered = Cases.Select(row => row.GameId).Concat(NamedCases.Select(row => row.GameId)).Concat(CoveredElsewhere).Distinct();

        SimpleGames.All.Select(row => row.GameId.ToString()).Should().BeEquivalentTo(covered);
    }

    private static SimpleGameDefinition RowOf(string gameId) => SimpleGames.All.Single(row => row.GameId == GameId.From(gameId));

    /// <summary>
    /// The paths the first installer of the row that supports the archive puts its files at, or null when none does.
    /// The install job takes installers in order and keeps the first that succeeds; so does this.
    /// </summary>
    private async Task<string[]?> Install(SimpleGameDefinition row, string[] archivePaths, string? archiveFileName = null)
    {
        var loadout = await CreateLoadout();
        var archive = archiveFileName is null ? await AddFromPaths(archivePaths) : await AddArchiveNamed(archiveFileName, archivePaths);
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();

        foreach (var installer in new SimpleGame(ServiceProvider, row).LibraryItemInstallers)
        {
            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
            };

            var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);
            if (result.IsSuccess is false) continue;

            var committed = (await tx.Commit()).Remap(group);
            return committed.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(file => ((GamePath)file.TargetPath).ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        return null;
    }
}
