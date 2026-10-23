using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;

public abstract class AMorrowindIntrinsicFilesTest<TTest>(ITestOutputHelper helper) : AOpenMwTest<TTest>(helper)
    where TTest : class
{
    protected static readonly LocationId OpenMwConfig = CreationEngine.Morrowind.Morrowind.OpenMwConfig;

    protected async Task<IEnumerable<GamePath>> OfferedFiles()
    {
        var loadout = await CreateLoadout();
        return ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout).Keys;
    }

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
    protected override int[] RootsWithConfig => [];

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
    public async Task EmitsNoDiagnostic() => (await Diagnose()).Should().BeEmpty();
}

/// <summary>One openmw.cfg: its folder is the location and the file is offered with Morrowind.ini, and nothing else under it is.</summary>
public class MorrowindWithOpenMwTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithOpenMwTests>(helper)
{
    protected override int[] RootsWithConfig => [1];

    [Fact]
    public async Task OffersTheConfigFileAndTheIni()
    {
        (await OfferedFiles()).Should().BeEquivalentTo([new GamePath(OpenMwConfig, "openmw.cfg"), new GamePath(LocationId.Game, "Morrowind.ini")]);
        GameInstallation.Locations[OpenMwConfig].Path.Should().Be(Roots.Candidates[1]);
        (await Diagnose()).Should().BeEmpty();
    }

    [Fact]
    public void UsesTheOpenMwMechanismAndIgnoresTheMasterFlag()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<OpenMwMechanism>();
        Game.MasterFlagIgnored(GameInstallation).Should().BeTrue();
    }

    [Fact]
    public void SortsAsOpenMw() => Game.LootGame(GameInstallation).Should().Be(LootGame.OpenMW);

    [Fact]
    public void BuiltInScriptsLoadEarly() => Game.EarlyLoadingPlugins(GameInstallation).Should().Equal("builtin.omwscripts");

    [Theory]
    [InlineData("settings.cfg", true)]
    [InlineData("launcher.cfg", true)]
    [InlineData("sub/other.txt", true)]
    [InlineData("openmw.cfg", false)]
    public void OnlyTheConfigFileIsIndexed(string path, bool filtered) =>
        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(OpenMwConfig, path)).Should().Be(filtered);
}

/// <summary>Two openmw.cfg files: the app does not guess, so there is no location and the user is told.</summary>
public class MorrowindWithTwoOpenMwConfigsTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithTwoOpenMwConfigsTests>(helper)
{
    protected override int[] RootsWithConfig => [0, 1];

    [Fact]
    public async Task HasNoLocationAndNamesBothPaths()
    {
        (await OfferedFiles()).Should().BeEquivalentTo([new GamePath(LocationId.Game, "Morrowind.ini")]);
        GameInstallation.Locations.ContainsKey(OpenMwConfig).Should().BeFalse();

        var diagnostic = (await Diagnose()).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var summary = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain(Roots.Candidates[0].ToString()).And.Contain(Roots.Candidates[1].ToString());
    }
}
