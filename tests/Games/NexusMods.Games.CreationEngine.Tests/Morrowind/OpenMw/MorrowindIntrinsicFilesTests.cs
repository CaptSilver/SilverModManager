using FluentAssertions;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

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

/// <summary>No openmw.cfg anywhere: the game has no OpenMW location and offers no intrinsic file.</summary>
public class MorrowindWithoutOpenMwTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithoutOpenMwTests>(helper)
{
    protected override int[] RootsWithConfig => [];

    [Fact]
    public async Task OffersNoIntrinsicFileAndHasNoLocation()
    {
        (await OfferedFiles()).Should().BeEmpty();
        GameInstallation.Locations.ContainsKey(OpenMwConfig).Should().BeFalse();
    }

    [Fact]
    public async Task EmitsNoDiagnostic() => (await Diagnose()).Should().BeEmpty();
}

/// <summary>One openmw.cfg: its folder is the location and the file is offered, and nothing else under it is.</summary>
public class MorrowindWithOpenMwTests(ITestOutputHelper helper) : AMorrowindIntrinsicFilesTest<MorrowindWithOpenMwTests>(helper)
{
    protected override int[] RootsWithConfig => [1];

    [Fact]
    public async Task OffersExactlyTheConfigFile()
    {
        (await OfferedFiles()).Should().BeEquivalentTo([new GamePath(OpenMwConfig, "openmw.cfg")]);
        GameInstallation.Locations[OpenMwConfig].Path.Should().Be(Roots.Candidates[1]);
        (await Diagnose()).Should().BeEmpty();
    }

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
        (await OfferedFiles()).Should().BeEmpty();
        GameInstallation.Locations.ContainsKey(OpenMwConfig).Should().BeFalse();

        var diagnostic = (await Diagnose()).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var summary = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain(Roots.Candidates[0].ToString()).And.Contain(Roots.Candidates[1].ToString());
    }
}
