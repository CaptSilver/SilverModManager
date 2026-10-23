using FluentAssertions;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

public class TestSoulsLaunchTests(ITestOutputHelper helper) : AModdedTestSoulsTest<TestSoulsLaunchTests>(helper)
{
    private RunFromSoftGame<TestSoulsGame> Tool => ServiceProvider.GetServices<ITool>().OfType<RunFromSoftGame<TestSoulsGame>>().Single();

    [Fact]
    public async Task TheDryRunSpecEndsWithMe3LaunchingTheGameByItsOwnNameFromTheHost()
    {
        var loadout = await CreateModdedLoadout();
        var host = GameInstallation.Locations[LocationId.Me3Host].Path;
        var mods = GameInstallation.Locations[LocationId.Me3Mods].Path;

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.ReplacesCommand.Should().BeTrue();
        var me3 = (host / Me3HostFiles.Binary).GetFullPath();
        var start = Array.IndexOf(spec.ExtraArgs.ToArray(), me3);
        start.Should().BeGreaterThan(-1, "the command starts me3 from the host copy");
        spec.ExtraArgs.Skip(start).Should().Equal(
            me3, "launch", "-p", (mods / Me3ModsLayout.ProfileFileName).GetFullPath(),
            "-g", "testsouls", "--windows-binaries-dir", (host / "bin/win64").GetFullPath());
    }

    [Fact]
    public async Task TheMissingHostMessageComesFromTheLoader()
    {
        var loadout = await CreateModdedLoadout();
        var loader = Substitute.For<IFromSoftModLoader>();
        loader.IsModded(Arg.Any<Loadout.ReadOnly>()).Returns(true);
        loader.HasHost(Arg.Any<Loadout.ReadOnly>()).Returns(false);
        loader.MissingHostMessage(loadout.Name).Returns("install the other loader");
        var game = new TestSoulsGame(ServiceProvider, _ => loader);

        var act = async () => await new RunFromSoftGame<TestSoulsGame>(ServiceProvider, game).GetLaunchSpec(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("install the other loader");
    }

    [Fact]
    public async Task Me3ReceivesAHomeUnderTheGamesOwnFolder()
    {
        var loadout = await CreateModdedLoadout();
        var home = FromSoftPaths.InstallationRoot(TestSoulsDefinition.Layout, Settings, FileSystem, "424242") / "me3-home";

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Environment["XDG_DATA_HOME"].Should().StartWith(home.GetFullPath()).And.Contain("TestSouls/424242/me3-home");
    }

    [Fact]
    public async Task FilesOfEldenRingsFoldersWrittenIntoTheUpperFolderAreUnexpectedBecauseTheBaseAllowlistHasNoFolders()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        // Written after Prepare, so they are the session's own writes and not part of the copied configuration layer.
        // Neither name matches one of the runtime patterns such as *.ini.
        var upper = layout.Upper.GetFullPath();
        Directory.CreateDirectory(Path.Combine(upper, "mods"));
        Directory.CreateDirectory(Path.Combine(upper, "SeamlessCoop"));
        await File.WriteAllTextAsync(Path.Combine(upper, "mods/x.bin"), "x");
        await File.WriteAllTextAsync(Path.Combine(upper, "SeamlessCoop/ersc_data.bin"), "x");
        await Gate.Finish(ContextOf(loadout));

        UpperdirAudit.PendingFindings(layout).Should().BeEquivalentTo(
        [
            new AuditFinding(AuditFindingKind.Unexpected, "mods/x.bin"),
            new AuditFinding(AuditFindingKind.Unexpected, "SeamlessCoop/ersc_data.bin"),
        ]);
    }
}
