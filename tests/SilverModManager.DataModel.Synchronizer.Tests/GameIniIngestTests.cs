using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A full apply after the game rewrote an owned INI: the setting the player changed in the game is kept in the
/// loadout's copy and in the file, and a setting the app manages is put back.
/// </summary>
public class GameIniIngestTests(ITestOutputHelper helper) : AIsolatedGameTest<GameIniIngestTests, IniStubbedGame>(helper)
{
    private static readonly byte[] UserIni = "[Display]\r\niSize W=2560 ; wide\r\niSize H=1440\r\n\r\n[General]\r\nsLanguage=ENGLISH\r\n"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<IniStubbedGame>()
        .AddUniversalGameLocator<IniStubbedGame>(Version.Parse("0.0.0"));

    private AbsolutePath Live => GameInstallation.Locations.ToAbsolutePath(IniStubbedGame.MainIni);

    private async Task<Loadout.ReadOnly> AppliedLoadout()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        return await Synchronizer.Synchronize(Refresh(loadout));
    }

    private async Task GameEdits(Action<IniDocument> edit)
    {
        var document = IniDocument.Parse(await Live.ReadAllBytesAsync());
        edit(document);
        await Live.WriteAllBytesAsync(document.ToBytes());
    }

    private LoadoutIniState.ReadOnly State(Loadout.ReadOnly loadout) =>
        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle(s => (GamePath)s.Path == IniStubbedGame.MainIni).Subject;

    [Fact]
    public async Task ASettingChangedInTheGameIsAdoptedAndSurvivesTheApply()
    {
        var loadout = await AppliedLoadout();
        await GameEdits(ini => ini.Set("Display", "iSize W", "1920"));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        var state = State(Refresh(loadout));
        var baseline = IniDocument.Parse(await FileStore.Load(state.Baseline));
        baseline.Get("Display", "iSize W").Should().Be("1920");
        baseline.Get("Archive", "bInvalidateOlderFiles").Should().BeNull("the app's own key is not part of the player's copy");

        var written = await Live.ReadAllBytesAsync();
        var live = IniDocument.Parse(written);
        live.Get("Display", "iSize W").Should().Be("1920", "the apply that adopts the setting also writes it");
        live.Get("Archive", "bInvalidateOlderFiles").Should().Be("1");
        LoadoutIniState.LastBakedHash.Get(state).Should().Be(written.xxHash3());
    }

    [Fact]
    public async Task AnAdoptedSettingStaysThroughTheNextApply()
    {
        var loadout = await AppliedLoadout();
        await GameEdits(ini => ini.Set("Display", "iSize W", "1920"));
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var afterFirst = await Live.ReadAllBytesAsync();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await Live.ReadAllBytesAsync()).Should().Equal(afterFirst);
        IniDocument.Parse(await FileStore.Load(State(Refresh(loadout)).Baseline)).Get("Display", "iSize W").Should().Be("1920");
    }

    [Fact]
    public async Task ASettingTheAppManagesIsPutBackAndTheBaselineIsLeftAlone()
    {
        var loadout = await AppliedLoadout();
        var baseline = State(Refresh(loadout)).Baseline;
        await GameEdits(ini => ini.Set("Archive", "bInvalidateOlderFiles", "0"));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("Archive", "bInvalidateOlderFiles").Should().Be("1");
        State(Refresh(loadout)).Baseline.Should().Be(baseline);
    }

    [Fact]
    public async Task AnApplyWithNoChangeByTheGameLeavesTheBaselineAlone()
    {
        var loadout = await AppliedLoadout();
        var before = State(Refresh(loadout));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        var after = State(Refresh(loadout));
        after.Baseline.Should().Be(before.Baseline);
        LoadoutIniState.LastBakedHash.Get(after).Should().Be(LoadoutIniState.LastBakedHash.Get(before));
    }
}
