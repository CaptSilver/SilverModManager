using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.DataModel;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A Wabbajack import made before the INIs went through the bake holds them as files at their real paths in the game
/// settings group. The pass turns each into the loadout's baseline for that path, so the bake is the only writer.
/// </summary>
public class ImportedIniFilesPassTests(ITestOutputHelper helper) : AIsolatedGameTest<ImportedIniFilesPassTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout4.ini");
    private static readonly GamePath PrefsIni = new(LocationId.Preferences, "Fallout4Prefs.ini");
    private const string MainText = "[General]\nsLanguage=EN\n";
    private const string PrefsText = "[Display]\niSize W=1920\n";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private ImportedIniFilesPass Pass => new(Connection, NullLogger<ImportedIniFilesPass>.Instance);

    private async Task AddGroups(Loadout.ReadOnly loadout, bool iniInMod = false)
    {
        using var tx = Connection.BeginTransaction();
        var settings = AddEmptyGroup(tx, loadout.LoadoutId, "Game settings");
        tx.Add(settings.Value, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        AddFile(tx, loadout.LoadoutId, settings, MainIni, MainText);
        AddFile(tx, loadout.LoadoutId, settings, PrefsIni, PrefsText);
        AddFile(tx, loadout.LoadoutId, settings, new GamePath(LocationId.Game, "INI Tweaks/Loadout [Fallout4.ini].ini"), "[Display]\nfMaxTime=0.1\n");

        var mod = AddEmptyGroup(tx, loadout.LoadoutId, "A mod");
        if (iniInMod) AddFile(tx, loadout.LoadoutId, mod, MainIni, "[General]\nsLanguage=FR\n");
        await tx.Commit();
    }

    private IEnumerable<GamePath> FilePaths(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(f => (GamePath)f.AsLoadoutItemWithTargetPath().TargetPath);

    [Fact]
    public async Task EachIniFileOfTheSettingsGroupBecomesABaselineAndLeavesItsRealPath()
    {
        var loadout = await CreateLoadout();
        await AddGroups(loadout);

        var moved = await Pass.Run();

        moved.Should().Be(2);
        var states = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).ToDictionary(s => (GamePath)s.Path, s => s.Baseline);
        states.Keys.Should().BeEquivalentTo([MainIni, PrefsIni]);
        states[MainIni].Should().Be(System.Text.Encoding.UTF8.GetBytes(MainText).xxHash3());
        states[PrefsIni].Should().Be(System.Text.Encoding.UTF8.GetBytes(PrefsText).xxHash3());
        FilePaths(loadout).Should().NotContain([MainIni, PrefsIni]);
        FilePaths(loadout).Should().Contain(new GamePath(LocationId.Game, "INI Tweaks/Loadout [Fallout4.ini].ini"), "only the INIs the bake owns move");
    }

    [Fact]
    public async Task ARunWithNothingLeftToMoveChangesNothing()
    {
        var loadout = await CreateLoadout();
        await AddGroups(loadout);
        await Pass.Run();

        (await Pass.Run()).Should().Be(0);

        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().HaveCount(2);
    }

    [Fact]
    public async Task AFileOfAnotherGroupAtTheSamePathIsLeftAlone()
    {
        var loadout = await CreateLoadout();
        await AddGroups(loadout, iniInMod: true);

        await Pass.Run();

        FilePaths(loadout).Should().ContainSingle(p => p == MainIni, "the mod's own file is not a profile file");
    }

    [Fact]
    public async Task ABaselineTheBakeAlreadyTookIsKeptAndTheFileStillLeaves()
    {
        var loadout = await CreateLoadout();
        var taken = "[General]\nsLanguage=DE\n"u8.ToArray();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = MainIni.ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = taken.xxHash3(),
            };
            await tx.Commit();
        }
        await AddGroups(loadout);

        await Pass.Run();

        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single(s => (GamePath)s.Path == MainIni).Baseline.Should().Be(taken.xxHash3());
        FilePaths(loadout).Should().NotContain(MainIni);
    }
}
