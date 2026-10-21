using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Games.FromSoft.Tests.Overlay;

public class RunEldenRingGameTests(ITestOutputHelper helper) : AModdedEldenRingTest<RunEldenRingGameTests>(helper)
{
    private RunEldenRingGame Tool => ServiceProvider.GetServices<ITool>().OfType<RunEldenRingGame>().Single();

    private AbsolutePath PrimaryExe => GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));

    private AbsolutePath Me3 => GameInstallation.Locations[LocationId.Me3Host].Path / Me3HostFiles.Binary;

    [Fact]
    public async Task AnUnmoddedLoadoutResolvesToThePrimaryExeWithNoArgumentsAndKeepsSteamsCommand()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(PrimaryExe);
        spec.ExtraArgs.Should().BeEmpty();
        spec.ReplacesCommand.Should().BeFalse();
    }

    [Fact]
    public async Task TheRunVerbWithAnUnmoddedLoadoutKeepsSteamsChainWithTheAntiCheatLauncher()
    {
        var loadout = await CreateLoadout();
        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);
        var root = GameInstallation.Locations[LocationId.Game].Path;
        // What Steam expands %command% to: the anti-cheat launcher, not eldenring.exe, is in the exe slot.
        var launcher = (root / "Game/start_protected_game.exe").GetFullPath();
        string[] steam = ["/steam/reaper", "SteamLaunch", "AppId=1245620", "--", "/steam/proton", "waitforexitandrun", launcher];

        var plan = LaunchPlanner.Compose(steam, root, PrimaryExe, spec, [], []);

        // Putting eldenring.exe in the launcher's place would start the game without its anti-cheat, offline only.
        plan.Process.Argv.Should().Equal(steam);
        plan.Substitution!.AppId.Should().Be("1245620");
    }

    [Fact]
    public async Task AModdedLoadoutResolvesToBwrapWithTheOverlayCommandAndReplacesSteamsCommand()
    {
        var loadout = await CreateModdedLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.ReplacesCommand.Should().BeTrue();
        spec.Exe.GetFullPath().Should().Be("/fake/bin/bwrap");
        var expected = OverlayCommand.Build(LayoutOf(loadout), 1000, 1001, Me3,
            GameInstallation.Locations[LocationId.Me3Mods].Path / Me3ModsLayout.ProfileFileName,
            GameInstallation.Locations[LocationId.Me3Host].Path / "bin/win64");
        spec.ExtraArgs.Should().Equal(expected.Skip(1));
    }

    [Fact]
    public async Task AModdedSpecNeverStartsAFileUnderTheGameLocationAndMe3IsTheHostCopy()
    {
        var loadout = await CreateModdedLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.InFolder(GameInstallation.Locations[LocationId.Game].Path).Should().BeFalse();
        spec.ExtraArgs.Should().Contain(Me3.GetFullPath());
        Me3.InFolder(GameInstallation.Locations[LocationId.Game].Path).Should().BeFalse();
    }

    [Fact]
    public async Task AModdedLoadoutWithoutTheHostThrowsAPlainMessage()
    {
        var loadout = await CreateLoadout();
        var path = TemporaryFileManager.CreateFolder().Path / "Weapons.zip";
        await TestArchives.Write(path, "parts/wp_a.dcx");
        var archive = await RegisterLocalArchive(path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId,
            installer: Game.LibraryItemInstallers.OfType<NexusMods.Games.FromSoft.Installers.Me3PackageInstaller>().Single());

        var act = async () => await Tool.GetLaunchSpec(Refresh(loadout), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("no me3");
    }

    [Fact]
    public async Task ModdedWithoutBwrapThrowsAPlainMessage()
    {
        var loadout = await CreateModdedLoadout();
        Executables.Bwrap = null;

        var act = async () => await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("bwrap was not found");
    }

    [Fact]
    public async Task ModdedPlayStartsTheBwrapCommandThroughTheProcessFactoryAndNeverTheToolRunner()
    {
        var loadout = await CreateModdedLoadout();

        await Tool.Execute(loadout, CancellationToken.None, []);

        var started = Processes.Started.Should().ContainSingle().Subject;
        started.Argv[0].Should().Be("/fake/bin/bwrap");
        started.Argv.Should().Contain("--unshare-user").And.Contain(Me3.GetFullPath());
        ServiceProvider.GetRequiredService<FakeToolRunner>().Calls.Should().BeEmpty();
        OsInterop.DidNotReceiveWithAnyArgs().OpenUri(default!);
    }

    [Fact]
    public async Task ModdedPlayHandsTheChildMe3sHostAndModsFoldersAndTheUpperFolderInThePressureVesselVariable()
    {
        var loadout = await CreateModdedLoadout();

        await Tool.Execute(loadout, CancellationToken.None, []);

        // Merged at spawn by GameProcess, never carried on the spec. Whether me3 passes the variable on to its own
        // pressure-vessel has not been run; a played session decides it.
        var environment = GameProcess.BuildEnvironment(new Dictionary<string, string>(), Processes.Started.Single());
        var roots = environment["PRESSURE_VESSEL_FILESYSTEMS_RW"].Split(':');
        roots.Should().Contain(GameInstallation.Locations[LocationId.Me3Mods].Path.GetFullPath());
        roots.Should().Contain(GameInstallation.Locations[LocationId.Me3Host].Path.GetFullPath());
        roots.Should().Contain(LayoutOf(loadout).Upper.GetFullPath());
    }

    [Fact]
    public async Task ModdedPlayKeepsMe3sCacheConfigDataAndStateUnderTheDataRootWhateverTheInheritedEnvironmentSays()
    {
        var loadout = await CreateModdedLoadout();
        var home = ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>()
            .DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>()) / "EldenRing" / "1245620" / "me3-home";

        await Tool.Execute(loadout, CancellationToken.None, []);

        var inherited = new Dictionary<string, string>
        {
            ["XDG_CACHE_HOME"] = "/home/someone/.cache", ["XDG_CONFIG_HOME"] = "/home/someone/.config",
            ["XDG_DATA_HOME"] = "/home/someone/.local/share", ["XDG_STATE_HOME"] = "/home/someone/.local/state",
        };
        var environment = GameProcess.BuildEnvironment(inherited, Processes.Started.Single());
        environment["XDG_CACHE_HOME"].Should().Be((home / "cache").GetFullPath());
        environment["XDG_CONFIG_HOME"].Should().Be((home / "config").GetFullPath());
        environment["XDG_DATA_HOME"].Should().Be((home / "data").GetFullPath());
        environment["XDG_STATE_HOME"].Should().Be((home / "state").GetFullPath());
        environment["PRESSURE_VESSEL_FILESYSTEMS_RW"].Split(':').Should().Contain(home.GetFullPath());
    }

    [Fact]
    public async Task TheRunVerbPathCarriesMe3sHomeInTheSpecToo()
    {
        var loadout = await CreateModdedLoadout();
        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        var plan = LaunchPlanner.Compose([], GameInstallation.Locations[LocationId.Game].Path, PrimaryExe, spec, Tool.FilesystemRootsOf(loadout), []);

        plan.Process.Environment.Should().ContainKeys("XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME");
        plan.Process.Environment!["XDG_DATA_HOME"].Should().EndWith("/EldenRing/1245620/me3-home/data");
    }

    [Fact]
    public async Task TheMe3HomeFoldersAreCreatedBeforeTheProcessStarts()
    {
        var loadout = await CreateModdedLoadout();
        var home = ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>()
            .DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>()) / "EldenRing" / "1245620" / "me3-home";
        var created = false;
        Processes.OnStart = () => created = new[] { "cache", "config", "data", "state" }.All(name => (home / name).DirectoryExists());

        await Tool.Execute(loadout, CancellationToken.None, []);

        created.Should().BeTrue("the container can only share me3's home folders if they exist when it starts");
    }

    [Fact]
    public async Task ModdedPlayRunsTheOverlayGateAroundTheProcessThroughTheExternalPath()
    {
        var loadout = await CreateModdedLoadout();
        var upper = LayoutOf(loadout).Upper.GetFullPath();
        var upperAtStart = false;
        Processes.OnStart = () => upperAtStart = Directory.Exists(upper);

        await Tool.Execute(loadout, CancellationToken.None, []);

        upperAtStart.Should().BeTrue("the gate's Prepare creates the upper folder before the game starts");
        File.Exists(LayoutOf(loadout).SessionState.GetFullPath()).Should().BeTrue();
    }

    [Fact]
    public async Task ModdedPlayWithAVetoingGateNeverStartsTheProcessAndSaysWhy()
    {
        var loadout = await CreateModdedLoadout();
        Probe.Failure = "Operation not permitted";

        var act = async () => await Tool.Execute(loadout, CancellationToken.None, []);

        (await act.Should().ThrowAsync<GameLaunchException>()).Which.Message.Should().Contain("Operation not permitted");
        Processes.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task TheRunVerbPathRunsTheOverlayGateToo()
    {
        var loadout = await CreateModdedLoadout();
        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);
        var plan = LaunchPlanner.Compose([], GameInstallation.Locations[LocationId.Game].Path, PrimaryExe, spec, [], []);
        var session = ServiceProvider.GetRequiredService<LaunchSession>();
        var upper = LayoutOf(loadout).Upper.GetFullPath();
        var upperAtStart = false;
        Processes.OnStart = () => upperAtStart = Directory.Exists(upper);

        var result = await session.RunAsync(ContextOf(loadout), () => plan);
        session.Release();

        result.Message.Should().BeNull();
        upperAtStart.Should().BeTrue("the gate's Prepare creates the upper folder before the game starts");
        Processes.Started.Single().Argv[0].Should().Be("/fake/bin/bwrap");
    }

    [Fact]
    public async Task ModdedPlayWithArgumentsIsRefusedLoudly()
    {
        var loadout = await CreateModdedLoadout();

        var act = async () => await Tool.Execute(loadout, CancellationToken.None, ["-windowed"]);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("-windowed");
        Processes.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task UnmoddedPlayGoesThroughTheSteamUriAsForAnyGame()
    {
        var loadout = await CreateLoadout();
        using var cancelOnOpen = new CancellationTokenSource();
        OsInterop.When(x => x.OpenUri(Arg.Any<Uri>())).Do(_ => cancelOnOpen.Cancel());

        await Tool.Execute(loadout, cancelOnOpen.Token, []);

        OsInterop.Received(1).OpenUri(new Uri("steam://run/1245620"));
        Processes.Started.Should().BeEmpty();
        ServiceProvider.GetRequiredService<FakeToolRunner>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFilesystemRootsNameTheDeploymentRootsAndTheThreeFoldersTheBackendsDoNotReport()
    {
        var loadout = await CreateModdedLoadout();

        var roots = Tool.FilesystemRootsOf(loadout);

        roots.Should().Contain(ServiceProvider.GetRequiredService<DeploymentLaunchRoots>().For(GameInstallation, loadout.InstallationId));
        roots.Should().Contain([
            GameInstallation.Locations[LocationId.Me3Mods].Path.GetFullPath(),
            GameInstallation.Locations[LocationId.Me3Host].Path.GetFullPath(),
            LayoutOf(loadout).Upper.GetFullPath(),
        ]);
    }
}
