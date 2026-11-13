using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Games;
using Xunit;
using static SilverModManager.Games.CreationEngine.OpenMw.OpenMwConstants;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind;

public abstract class AMorrowindIntrinsicFilesTest<TTest>(ITestOutputHelper helper) : AMorrowindTest<TTest>(helper)
    where TTest : class
{
    protected async Task<IEnumerable<GamePath>> OfferedFiles()
    {
        var loadout = await CreateLoadout();
        return ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout).Keys;
    }

    /// <summary>
    /// Morrowind.ini is the loadout's own copy like any owned INI, so once the first sync has written the file a
    /// suggestion says the next apply takes it over. That is the only notice a healthy setup carries.
    /// </summary>
    protected static void OnlyTheIniTakeOverSuggestion(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Suggestion)
            .And.OnlyContain(d => d.FormatSummary(new PlainDiagnosticWriter()).Contains("Morrowind.ini"));

    protected async Task<List<Diagnostic>> Diagnose()
    {
        var loadout = await CreateLoadout();
        var result = new List<Diagnostic>();
        foreach (var emitter in Game.DiagnosticEmitters.OfType<ILoadoutDiagnosticEmitter>())
        {
#pragma warning disable CS0618
            await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None))
                result.Add(diagnostic);
#pragma warning restore CS0618
        }
        return result;
    }
}

/// <summary>No openmw.cfg anywhere: the game has no OpenMW location and offers Morrowind.ini alone.</summary>
public class MorrowindWithoutOpenMwTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithoutOpenMwTests>(helper)
{
    [Fact]
    public async Task OffersTheIniAndHasNoLocation()
    {
        (await OfferedFiles()).Should().BeEquivalentTo([new GamePath(LocationId.Game, "Morrowind.ini")]);
        GameInstallation.Locations.ContainsKey(OpenMwConfig).Should().BeFalse();
    }

    [Fact]
    public void KeepsTheIniMechanismAndReadsTheMasterFlag()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<MorrowindIniMechanism>().Which.OrdersByTimestamp.Should().BeTrue();
        Game.MasterFlagIgnored(GameInstallation).Should().BeFalse();
    }

    [Fact]
    public void SortsWithTheMorrowindMasterlist() => Game.LootGame(GameInstallation).Should().Be(LootGame.Morrowind);

    [Fact]
    public void LoadsNothingEarly() => Game.EarlyLoadingPlugins(GameInstallation).Should().BeEmpty();

    [Fact]
    public async Task EmitsNothingButTheIniTakeOverSuggestion() => OnlyTheIniTakeOverSuggestion(await Diagnose());
}

/// <summary>An openmw.cfg beside the install changes nothing about how the Morrowind row works: the ini is its list.</summary>
public class MorrowindWithAnOpenMwConfigTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithAnOpenMwConfigTests>(helper)
{
    protected override string? OpenMwConfigText => Install.Config();

    [Fact]
    public async Task OffersTheIniAlone()
    {
        (await OfferedFiles()).Should().BeEquivalentTo([new GamePath(LocationId.Game, "Morrowind.ini")]);
        OnlyTheIniTakeOverSuggestion(await Diagnose());
    }

    [Fact]
    public void KeepsTheConfigFolderAsAnInertLocation() =>
        GameInstallation.Locations[OpenMwConfig].Path.Should().Be(Roots.NativeConfig);

    [Fact]
    public void KeepsTheIniMechanismAndReadsTheMasterFlag()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<MorrowindIniMechanism>().Which.OrdersByTimestamp.Should().BeTrue();
        Game.MasterFlagIgnored(GameInstallation).Should().BeFalse();
    }

    [Fact]
    public void SortsWithTheMorrowindMasterlist() => Game.LootGame(GameInstallation).Should().Be(LootGame.Morrowind);

    [Fact]
    public void LoadsNothingEarly() => Game.EarlyLoadingPlugins(GameInstallation).Should().BeEmpty();
}
