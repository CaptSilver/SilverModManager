using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Deployment;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class DryRunTests : IDisposable
{
    private const string GameRoot = "/games/steamapps/common/Skyrim Special Edition";

    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-dryrun-").FullName;
    private readonly IInstallationLockFactory _lockFactory = Substitute.For<IInstallationLockFactory>();
    private readonly ILaunchSynchronizer _synchronizer = Substitute.For<ILaunchSynchronizer>();
    private readonly IGameProcessFactory _processes = Substitute.For<IGameProcessFactory>();
    private readonly IDesktopNotifier _notifier = Substitute.For<IDesktopNotifier>();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly INexusCredentialSummary _credentials = Substitute.For<INexusCredentialSummary>();
    private readonly ILaunchMounts _mounts = Substitute.For<ILaunchMounts>();
    private readonly LaunchSession _session;
    private readonly LaunchContext _context;

    public DryRunTests()
    {
        _mounts.For(Arg.Any<LaunchContext>()).Returns(LaunchMounts.None);
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Skyrim Special Edition");
        var folder = Path_(GameRoot);
        var installation = new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "489830",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));

        _context = new LaunchContext(installation, LoadoutId.From(1), "My Loadout", []);
        var markers = new FakeLaunchFinishMarkers { ReadWith = () => null };
        var active = new ActiveLaunchSessions();
        _session = new LaunchSession(_lockFactory, active, _synchronizer, new LaunchFinish(_synchronizer, markers, NullLogger<LaunchFinish>.Instance), TestFinishers.For(_lockFactory, active, [], new LaunchFinish(_synchronizer, markers, NullLogger<LaunchFinish>.Instance), markers), markers, _processes, [], _notifier, NullLogger<LaunchSession>.Instance, _error, credentials: _credentials, mounts: _mounts);
    }

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private static AbsolutePath Path_(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private static string[] SteamCommand(string exe, params string[] gameArgs) =>
    [
        "/steam/ubuntu12_32/steam-launch-wrapper", "--", "/steam/ubuntu12_32/reaper", "SteamLaunch", "AppId=489830",
        "--", "/steam/entry-point", "--verb=waitforexitandrun", "--", "/steam/proton", "waitforexitandrun", exe, ..gameArgs,
    ];

    private static LaunchPlan Compose(string[] command, string specExe, params string[] extraArgs)
        => LaunchPlanner.Compose(command, Path_(GameRoot), Path_($"{GameRoot}/SkyrimSE.exe"), new LaunchSpec(Path_(specExe), extraArgs), [], []);

    [Fact]
    public void ThePrintedCommandIsTheOneTheSubstitutionProduced()
    {
        var plan = Compose(SteamCommand($"{GameRoot}/SkyrimSELauncher.exe", "-windowed"), $"{GameRoot}/skse64_loader.exe", "-forcesteamloader");

        var result = _session.DryRun(_context, () => plan, _output);

        result.Should().Be(new LaunchResult(0, null));
        var lines = _output.ToString().ReplaceLineEndings("\n").Split('\n');
        lines.Should().Contain($"Command: {string.Join(' ', plan.Substitution!.Argv)}");
        plan.Substitution.Argv.Should().Contain($"{GameRoot}/skse64_loader.exe").And.Contain("-forcesteamloader");
    }

    [Fact]
    public void ThePlanNamesTheLoadoutTheSubstitutionAndTheSingleSynchronize()
    {
        var plan = Compose(SteamCommand($"{GameRoot}/SkyrimSELauncher.exe"), $"{GameRoot}/skse64_loader.exe");

        _session.DryRun(_context, () => plan, _output);

        var text = _output.ToString();
        text.Should().Contain("My Loadout").And.Contain("Skyrim Special Edition");
        text.Should().Contain($"Run tool: {GameRoot}/skse64_loader.exe");
        text.Should().Contain("App id: 489830");
        text.Should().Contain("synchronize");
    }

    [Fact]
    public void ARootServedThroughAMountIsListedAfterTheRunTool()
    {
        var plan = Compose(SteamCommand($"{GameRoot}/SkyrimSELauncher.exe"), $"{GameRoot}/skse64_loader.exe");
        _mounts.For(_context).Returns(new LaunchMounts([], [new MountedRoot(new GamePath(LocationId.Game, "Data"), new DeploymentBackendId("fuse"), Path_($"{GameRoot}/Data"))]));

        _session.DryRun(_context, () => plan, _output);

        var lines = _output.ToString().ReplaceLineEndings("\n").Split('\n').ToList();
        lines.Should().Contain($"Mounted while the game runs: Game/Data (fuse) at {GameRoot}/Data");
        lines.IndexOf($"Mounted while the game runs: Game/Data (fuse) at {GameRoot}/Data").Should().Be(lines.IndexOf($"Run tool: {GameRoot}/skse64_loader.exe") + 1);
    }

    [Fact]
    public void ThePlanSaysNothingAboutMountsWhenNoRootIsServedThroughOne()
    {
        var plan = Compose(SteamCommand($"{GameRoot}/SkyrimSELauncher.exe"), $"{GameRoot}/skse64_loader.exe");
        _mounts.For(_context).Returns(new LaunchMounts([], []));

        _session.DryRun(_context, () => plan, _output);

        _output.ToString().Should().NotContain("Mounted while the game runs");
    }

    [Fact]
    public void ACommandWithNoRunToolIsPrintedAsItIs()
    {
        var command = new[] { "/usr/bin/true", "--flag" };

        _session.DryRun(_context, () => new LaunchPlan(new GameProcessSpec(command)), _output).ExitCode.Should().Be(0);

        var text = _output.ToString();
        text.Should().Contain("Run tool: none");
        text.Should().Contain("Command: /usr/bin/true --flag");
    }

    [Fact]
    public void NothingIsLockedAppliedOrStarted()
    {
        var plan = Compose(SteamCommand($"{GameRoot}/SkyrimSELauncher.exe"), $"{GameRoot}/skse64_loader.exe");

        _session.DryRun(_context, () => plan, _output);

        _lockFactory.ReceivedCalls().Should().BeEmpty();
        _synchronizer.ReceivedCalls().Should().BeEmpty();
        _processes.ReceivedCalls().Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(_runtimeDir).Should().BeEmpty();
    }

    [Fact]
    public void ARefusedSubstitutionFailsLikeARealLaunchWithNoOutputOnStdout()
    {
        var result = _session.DryRun(_context, () => Compose(["/usr/bin/true"], $"{GameRoot}/skse64_loader.exe"), _output);

        result.ExitCode.Should().Be(LaunchSession.FailureExitCode);
        result.Message.Should().Contain("/usr/bin/true");
        _error.ToString().Should().Contain("/usr/bin/true");
        _notifier.Received(1).Notify(Arg.Any<string>(), Arg.Is<string>(m => m.Contains("/usr/bin/true")));
        _output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ThePlanNamesTheWineDllOverrides()
    {
        WineDllOverride[] overrides =
        [
            new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
            new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
        ];
        var plan = new LaunchPlan(new GameProcessSpec(["/usr/bin/true"], WineDllOverrides: overrides));

        _session.DryRun(_context, () => plan, _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain("Wine DLL overrides: winmm=n,b;version=n,b");
    }

    [Fact]
    public void ThePlanSaysWhenThereAreNoWineDllOverrides()
    {
        _session.DryRun(_context, () => new LaunchPlan(new GameProcessSpec(["/usr/bin/true"])), _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain("Wine DLL overrides: none");
    }

    [Fact]
    public void ThePlanNamesTheLaunchEnvironment()
    {
        var plan = new LaunchPlan(new GameProcessSpec(["/usr/bin/true"], DefaultEnvironment: [new("PROTON_ENABLE_WAYLAND", "1"), new("PROTON_ENABLE_HDR", "1")]));

        _session.DryRun(_context, () => plan, _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain("Launch environment: PROTON_ENABLE_WAYLAND=1 PROTON_ENABLE_HDR=1");
    }

    [Fact]
    public void ThePlanSaysWhenThereIsNoLaunchEnvironment()
    {
        _session.DryRun(_context, () => new LaunchPlan(new GameProcessSpec(["/usr/bin/true"])), _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain("Launch environment: none");
    }

    [Theory]
    [InlineData(NexusCredentialKind.ApiKey, "--secrets-file /run/secrets.env", "Nexus credential: API key from --secrets-file /run/secrets.env")]
    [InlineData(NexusCredentialKind.ApiKey, "environment variable NEXUS_API_KEY", "Nexus credential: API key from environment variable NEXUS_API_KEY")]
    [InlineData(NexusCredentialKind.OAuthToken, "Secret Service", "Nexus credential: OAuth token from Secret Service")]
    [InlineData(NexusCredentialKind.OAuthToken, "private file", "Nexus credential: OAuth token from private file")]
    [InlineData(NexusCredentialKind.None, "", "Nexus credential: none")]
    public void ThePlanNamesTheNexusCredentialAndWhereItCameFrom(NexusCredentialKind kind, string source, string expected)
    {
        _credentials.Kind.Returns(kind);
        _credentials.Source.Returns(source);

        _session.DryRun(_context, () => new LaunchPlan(new GameProcessSpec(["/usr/bin/true"])), _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain(expected);
    }

    [Fact]
    public void WithoutACredentialSummaryTheCredentialLineSaysNone()
    {
        var markers = new FakeLaunchFinishMarkers { ReadWith = () => null };
        var active = new ActiveLaunchSessions();
        var session = new LaunchSession(_lockFactory, active, _synchronizer, new LaunchFinish(_synchronizer, markers, NullLogger<LaunchFinish>.Instance), TestFinishers.For(_lockFactory, active, [], new LaunchFinish(_synchronizer, markers, NullLogger<LaunchFinish>.Instance), markers), markers, _processes, [], _notifier, NullLogger<LaunchSession>.Instance, _error);

        session.DryRun(_context, () => new LaunchPlan(new GameProcessSpec(["/usr/bin/true"])), _output);

        _output.ToString().ReplaceLineEndings("\n").Split('\n').Should().Contain("Nexus credential: none");
    }
}
