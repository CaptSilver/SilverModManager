using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
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
        ("Simple.DarkestDungeon", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
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
        ("Simple.VampireBloodlines", ["Mod/data.bin", "readme.txt"], ["{Game}/vampire/Mod/data.bin", "{Game}/vampire/readme.txt"]),
        ("Simple.Control", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.BatmanArkhamCity", ["Mod/data.bin", "readme.txt"], ["{Game}/BmGame/Mod/data.bin", "{Game}/BmGame/readme.txt"]),
        ("Simple.AssettoCorsa", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.Borderlands", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.F123", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.KarrynsPrison", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MetalGearSolid2MasterCollection", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MetalGearSolid3MasterCollection", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.MirrorsEdge", ["Mod/data.bin", "readme.txt"], ["{Game}/TdGame/Mod/data.bin", "{Game}/TdGame/readme.txt"]),
        ("Simple.NierAutomata", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.TestDriveUnlimited2", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.TrackmaniaUnitedForever", ["Mod/data.bin", "readme.txt"], ["{Game}/GameData/Mod/data.bin", "{Game}/GameData/readme.txt"]),
        ("Simple.TrainSimulator", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.ValkyriaChronicles", ["Mod/data.bin", "readme.txt"], ["{Game}/Mod/data.bin", "{Game}/readme.txt"]),
        ("Simple.ZeusAndPoseidon", ["Mod/data.bin", "readme.txt"], ["{Game}/Adventures/Mod/data.bin", "{Game}/Adventures/readme.txt"]),
        ("Simple.AHatInTime", ["Mod/data.bin", "readme.txt"], ["{Game}/HatInTimeGame/Mods/Mod/data.bin", "{Game}/HatInTimeGame/Mods/readme.txt"]),
        ("Simple.BreakingWheel", ["Mod/data.bin", "readme.txt"], ["{Game}/ModdingTools/Mod/data.bin", "{Game}/ModdingTools/readme.txt"]),
        ("Simple.DawnOfMan", ["Mod/data.bin", "readme.txt"], ["{Game}/Mods/Mod/data.bin", "{Game}/Mods/readme.txt"]),
        ("Simple.Elex", ["Mod/data.bin", "readme.txt"], ["{Game}/data/packed/Mod/data.bin", "{Game}/data/packed/readme.txt"]),
        ("Simple.GreedFall", ["Mod/data.bin", "readme.txt"], ["{Game}/datalocal/Mod/data.bin", "{Game}/datalocal/readme.txt"]),
        ("Simple.GrimDawn", ["Mod/data.bin", "readme.txt"], ["{Game}/mods/Mod/data.bin", "{Game}/mods/readme.txt"]),
        ("Simple.StateOfDecay", ["Mod/data.bin", "readme.txt"], ["{Game}/game/Mod/data.bin", "{Game}/game/readme.txt"]),
        ("Simple.XRebirth", ["Mod/data.bin", "readme.txt"], ["{Game}/extensions/Mod/data.bin", "{Game}/extensions/readme.txt"]),
        ("Simple.TeamFortress2", ["Mod/data.bin", "readme.txt"], ["{Game}/tf/custom/Mod/data.bin", "{Game}/tf/custom/readme.txt"]),
        ("Simple.Starbound", ["Mod/data.bin", "readme.txt"], ["{Game}/mods/Mod/data.bin", "{Game}/mods/readme.txt"]),
    ];

    /// <summary>Layouts the games with a published layout cannot use.</summary>
    public static readonly (string GameId, string[] ArchivePaths)[] Refused =
    [
        ("Simple.DungeonSiege", ["readme.txt"]),
        ("Simple.DungeonSiege", ["Textures/a.dds"]),
        ("Simple.DungeonSiege2", ["readme.txt"]),
        ("Simple.DungeonSiege2", ["Mod/readme.txt"]),
    ];

    public static IEnumerable<object[]> CaseData() => Cases.Select(row => (object[])[row.GameId, row.ArchivePaths, row.Expected]);

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
    [MemberData(nameof(RefusedData))]
    public async Task AWrongLayout_IsNotSupported(string gameId, string[] archivePaths)
    {
        (await Install(RowOf(gameId), archivePaths)).Should().BeNull();
    }

    [Fact]
    public void EveryRow_HasAnInstallCase()
    {
        var covered = Cases.Select(row => row.GameId).Concat(CoveredElsewhere).Distinct();

        SimpleGames.All.Select(row => row.GameId.ToString()).Should().BeEquivalentTo(covered);
    }

    private static SimpleGameDefinition RowOf(string gameId) => SimpleGames.All.Single(row => row.GameId == GameId.From(gameId));

    /// <summary>
    /// The paths the first installer of the row that supports the archive puts its files at, or null when none does.
    /// The install job takes installers in order and keeps the first that succeeds; so does this.
    /// </summary>
    private async Task<string[]?> Install(SimpleGameDefinition row, string[] archivePaths)
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(archivePaths);
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
