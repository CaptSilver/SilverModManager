using System.Collections.Frozen;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Build;

public class BuildStampTests(ITestOutputHelper helper) : AModdedEldenRingTest<BuildStampTests>(helper)
{
    private static readonly BuildIdentity Original = new("2.7.1.0", EldenRingInstall.SteamBuildId.ToString(), EldenRingInstall.RegulationVersion, EldenRingInstall.RegulationHash);

    private BuildStampGate StampGate => ServiceProvider.GetServices<ILaunchGate>().OfType<BuildStampGate>().Single();

    private BuildStampStore Stamps => ServiceProvider.GetRequiredService<BuildStampStore>();

    private GameInstallMetadataId InstallationOf(Loadout.ReadOnly loadout) => loadout.InstallationId;

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<BuildDriftEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    private void SetSteamBuildId(string buildId)
    {
        EldenRingInstall.SetSteamBuildId(Install, buildId);
        Relocate();
    }

    private void SetExecutableVersion(Version version)
    {
        File.WriteAllBytes((RealGameFolder / "eldenring.exe").GetFullPath(), FakePe.WithVersion(version));
    }

    [Fact]
    public async Task TheGateStampsTheBuildAtTheFirstPrepare()
    {
        var loadout = await CreateLoadout();
        Stamps.Find(InstallationOf(loadout)).Should().BeNull();

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        Stamps.Find(InstallationOf(loadout)).Should().Be(Original);
    }

    [Fact]
    public async Task AStampIsKeptWhenTheBuildHasMovedAtALaterPrepare()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        SetExecutableVersion(new Version(2, 8, 0, 0));
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        Stamps.Find(InstallationOf(loadout)).Should().Be(Original);
    }

    // What the current build wrote before the regulation was tracked: the two install fields and nothing else.
    private async Task StampLikeAnOlderBuild(Loadout.ReadOnly loadout)
    {
        await Stamps.Record(InstallationOf(loadout), new BuildIdentity(Original.Exe, Original.SteamBuildId));
        Stamps.Find(InstallationOf(loadout)).Should().Be(new BuildIdentity(Original.Exe, Original.SteamBuildId));
    }

    [Fact]
    public async Task AStampFromBeforeTheRegulationWasTrackedProducesNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await StampLikeAnOlderBuild(loadout);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheGateBackfillsTheRegulationFieldsIntoAnOlderStamp()
    {
        var loadout = await CreateLoadout();
        await StampLikeAnOlderBuild(loadout);

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        Stamps.Find(InstallationOf(loadout)).Should().Be(Original);
        FromSoftBuildStamp.All(Connection.Db).Should().ContainSingle();
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABackfilledStampThenCatchesARegulationWrittenBehindTheApp()
    {
        var loadout = await CreateLoadout();
        await StampLikeAnOlderBuild(loadout);
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        var edited = RegulationFixture.BuildFromEntries(RegulationFixture.GoldenEntries, "11701001", EldenRingGame.Definition.Formats);
        await (RealGameFolder / "regulation.bin").WriteAllBytesAsync(edited);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        Describe(diagnostic).Should().Contain("tampered").And.Contain("regulation_version").And.Contain("11701000").And.Contain("11701001");
    }

    [Fact]
    public async Task AnOlderStampIsNotBackfilledWhenTheInstallHasMoved()
    {
        var loadout = await CreateLoadout();
        await StampLikeAnOlderBuild(loadout);

        SetExecutableVersion(new Version(2, 8, 0, 0));
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        // The live regulation may belong to the new build, so it says nothing about the stamped one.
        Stamps.Find(InstallationOf(loadout)).Should().Be(new BuildIdentity(Original.Exe, Original.SteamBuildId));
    }

    [Fact]
    public async Task TheGateNeverVetoes()
    {
        var loadout = await CreateLoadout();
        StampGate.Check(ContextOf(loadout)).Should().BeNull();

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);
        SetExecutableVersion(new Version(2, 8, 0, 0));

        StampGate.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnreadableBuildFailsThePrepareInsteadOfStampingAHalfIdentity()
    {
        var loadout = await CreateLoadout();
        File.WriteAllText((RealGameFolder / "eldenring.exe").GetFullPath(), "not a pe");

        var act = () => StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*eldenring.exe*");
        Stamps.Find(InstallationOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task TheGateRunsAfterTheOtherGates()
    {
        var gates = ServiceProvider.GetServices<ILaunchGate>().Select(g => g.GetType()).ToList();

        gates.IndexOf(typeof(BuildStampGate)).Should().Be(gates.Count - 1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnotherGameIsNeitherCheckedNorStamped()
    {
        var loadout = await CreateLoadout();
        var result = GameInstallation.LocatorResult;
        var other = new GameInstallation(new GameLocatorResult
        {
            Game = DispatchProxy.Create<IGameData, OtherGameProxy>(),
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
        }, GameInstallation.Locations);
        var context = ContextOf(loadout) with { Installation = other };

        StampGate.Check(context).Should().BeNull();
        await StampGate.Prepare(context, CancellationToken.None);

        Stamps.Find(InstallationOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnstampedLoadoutHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnchangedBuildHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AChangedSteamBuildIdIsAWarningNamingTheOldAndNewValue()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        SetSteamBuildId("16000000");

        var diagnostic = (await Diagnose(Refresh(loadout))).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var text = Describe(diagnostic);
        text.Should().Contain("steam_buildid").And.Contain("15000000").And.Contain("16000000");
        text.Should().NotContain("exe:");
        diagnostic.Actions.Should().Equal(BuildDriftEmitter.RestampAction(loadout.LoadoutId));
    }

    [Fact]
    public async Task AChangedExecutableVersionIsAWarningNamingTheExe()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        SetExecutableVersion(new Version(2, 8, 0, 0));

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Describe(diagnostic).Should().Contain("exe").And.Contain("2.7.1.0").And.Contain("2.8.0.0");
    }

    [Fact]
    public async Task RestampingTakesTheLiveBuildAndClearsTheDiagnostic()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);
        SetExecutableVersion(new Version(2, 8, 0, 0));
        (await Diagnose(loadout)).Should().ContainSingle();

        await Stamps.Restamp(loadout.LoadoutId);

        Stamps.Find(InstallationOf(loadout)).Should().Be(Original with { Exe = "2.8.0.0" });
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARestampKeepsASingleStampPerInstallation()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);
        SetExecutableVersion(new Version(2, 8, 0, 0));

        await Stamps.Restamp(loadout.LoadoutId);

        FromSoftBuildStamp.All(Connection.Db).Should().ContainSingle();
    }

    public class OtherGameProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_GameId" ? GameId.From("Other.Game") : null;
    }
}
