using SilverModManager.Abstractions.Games;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CliWrap;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests;

public class ToolRunnerTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _prefix;
    private readonly AbsolutePath _protonDir;
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _commands = [];
    private readonly ILogger<ToolRunner.ToolRunner> _logger = Substitute.For<ILogger<ToolRunner.ToolRunner>>();

    public ToolRunnerTests()
    {
        _steam = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        _prefix = _steam.Combine("steamapps/compatdata/42/pfx");
        _protonDir = _steam.Combine("steamapps/common/Proton 9.0");

        _fs.CreateDirectory(_protonDir);
        _fs.WriteAllText(_protonDir.Combine("proton"), "#!/usr/bin/env python3");
        _fs.WriteAllText(_protonDir.Combine("toolmanifest.vdf"), "");
        _fs.CreateDirectory(_prefix);
        _fs.WriteAllText(_prefix.Combine("system.reg"), "");

        _processRunner
            .RunAsync(Arg.Do<Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(_ => new CommandResult(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private ToolRunner.ToolRunner Runner(IReadOnlyList<string>? filesystemRoots = null, HostExecution? host = null, ISettingsManager? settings = null, bool withPlainWine = false, IStartedTools? startedTools = null)
    {
        var protonResolver = new ProtonResolver(new EmptyEnvironment());
        IToolRunStrategy[] strategies = withPlainWine
            ? [new SteamProtonStrategy(protonResolver), new PlainWineStrategy(_processRunner)]
            : [new SteamProtonStrategy(protonResolver)];
        return new ToolRunner.ToolRunner(
            new PrefixResolver(),
            strategies,
            _processRunner,
            RegistryWithMetadata(),
            LaunchRoots(filesystemRoots ?? []),
            host ?? UnmanagedGames.X64Host(),
            settings ?? UnmanagedGames.Settings(),
            startedTools ?? new StartedTools(),
            _logger);
    }

    // Plan rows for two symlink roots, both needing the same store folder, as the deployment plan records them.
    private static DeploymentLaunchRoots LaunchRoots(IReadOnlyList<string> roots)
    {
        var plans = Substitute.For<IDeploymentPlanStore>();
        plans.Load(Arg.Any<IDb>(), Arg.Any<GameInstallMetadataId>()).Returns(new DeploymentPlan(
        [
            new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Symlink),
            new DeploymentPlanRoot(new GamePath(LocationId.Saves, RelativePath.Empty), DeploymentBackendId.Symlink),
        ]));

        var backend = Substitute.For<IDeploymentBackend>();
        backend.Id.Returns(DeploymentBackendId.Symlink);
        backend.RequiredFilesystems(Arg.Any<GameInstallation>(), Arg.Any<GamePath>())
            .Returns(_ => roots.Select(r => FileSystem.Shared.FromUnsanitizedFullPath(r)).ToArray());

        return new DeploymentLaunchRoots(plans, [backend], Substitute.For<IConnection>());
    }

    private static IGameRegistry RegistryWithMetadata()
    {
        var registry = Substitute.For<IGameRegistry>();
        var metadata = default(GameInstallMetadata.ReadOnly);
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>())
            .Returns(call => { call[1] = metadata; return true; });
        return registry;
    }

    private GameInstallation SteamGame(bool withProvider = true)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = _steam.Combine("steamapps/common/Test Game");

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.WinePrefixDirectoryPath.Returns(_prefix);
        provider.SteamInstallPath.Returns(_steam);

        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            WinePrefixPath = _prefix,
            LinuxCompatabilityDataProvider = withProvider ? provider : null,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.SteamProton, _prefix),
        };

        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    private GameInstallation GogInWine()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Wine Game");
        var gameFolder = _fs.FromUnsanitizedFullPath("/wine/drive_c/GOG Games/Wine Game");
        var winePrefix = _fs.FromUnsanitizedFullPath("/wine");
        _fs.CreateDirectory(winePrefix);
        _fs.WriteAllText(winePrefix.Combine("system.reg"), "");

        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.GOG,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.PlainWine, winePrefix),
        };

        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    private AbsolutePath Exe => _fs.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    [Fact]
    public void SteamGame_PlansTheProtonScriptWithSteamsEnvironment()
    {
        var plan = Runner().Plan(SteamGame(), Exe, ["-IKnowWhatImDoing"], null);

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("SteamProton");
        plan.Argv.Should().Equal(_protonDir.Combine("proton").ToString(), "run", Exe.ToString(), "-IKnowWhatImDoing");
        plan.WorkingDirectory.Should().Be(Exe.Parent);
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = _prefix.Parent.ToString(),
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _steam.ToString(),
            ["WINEDEBUG"] = "-all,err+all",
        });
    }

    [Fact]
    public void AWorkingDirectoryOnTheCommandWins()
    {
        var folder = _fs.FromUnsanitizedFullPath("/tools/xEdit/Data");

        var plan = Runner().Plan(SteamGame(), Exe, [], null, workingDirectory: folder);

        plan.WorkingDirectory.Should().Be(folder, "the program starts where the user said, not next to itself");
    }

    [Fact]
    public async Task RunWindowsTool_StartsTheProgramInTheGivenWorkingDirectory()
    {
        var folder = _fs.FromUnsanitizedFullPath("/tools/xEdit/Data");

        await Runner().RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None, workingDirectory: folder);

        _commands.Should().ContainSingle().Which.WorkingDirPath.Should().Be(folder.ToString());
    }

    [Fact]
    public void SteamGame_AsksWineForErrorLines()
        => Runner().Plan(SteamGame(), Exe, [], null).Environment["WINEDEBUG"].Should().Be("-all,err+all");

    [Fact]
    public void ThePlanRunsProtonThroughResolvedPaths()
    {
        // Proton writes config_info from the paths it was started through, so a Steam root reached through a
        // symlinked parent (/home on an ostree host) would make every run rewrite what Steam recorded.
        var rawRoot = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"smm-resolved-{Guid.NewGuid():N}");
        try
        {
            NexusMods.Paths.FileSystem.Shared.CreateDirectory(rawRoot);
            // The temp root can itself sit behind a link (/home on an ostree host), which ResolvedPath follows.
            var root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(ResolvedPath.Of(rawRoot.ToString()));
            var real = root.Combine("real");
            var link = root.Combine("link");
            var realSteam = real.Combine("steam");
            var realProton = realSteam.Combine("steamapps/common/Proton 9.0");
            var realCompat = realSteam.Combine("steamapps/compatdata/42");
            NexusMods.Paths.FileSystem.Shared.CreateDirectory(realProton);
            NexusMods.Paths.FileSystem.Shared.CreateDirectory(realCompat.Combine("pfx"));
            NexusMods.Paths.FileSystem.Shared.WriteAllText(realProton.Combine("proton"), "");
            NexusMods.Paths.FileSystem.Shared.WriteAllText(realProton.Combine("toolmanifest.vdf"), "");
            Directory.CreateSymbolicLink(link.ToString(), real.ToString());

            var viaLink = link.Combine("steam");
            var game = Substitute.For<IGameData>();
            game.DisplayName.Returns("Test Game");
            var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
            provider.SteamInstallPath.Returns(viaLink);
            var installation = new GameInstallation(
                new GameLocatorResult
                {
                    Game = game,
                    Path = viaLink.Combine("steamapps/common/Test Game"),
                    Store = GameStore.Steam,
                    StoreIdentifier = "42",
                    LocatorIds = ImmutableArray<LocatorId>.Empty,
                    Locator = Substitute.For<IGameLocator>(),
                    Platform = OSPlatform.Windows,
                    LinuxCompatabilityDataProvider = provider,
                    Compatibility = new CompatibilityLayer(CompatibilityLayerKind.SteamProton, viaLink.Combine("steamapps/compatdata/42/pfx")),
                },
                GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty));
            var prefix = new ResolvedPrefix(viaLink.Combine("steamapps/compatdata/42/pfx"), CompatibilityLayerKind.SteamProton, null, 42);

            var plan = new SteamProtonStrategy(new ProtonResolver(new EmptyEnvironment()))
                .Plan(installation, prefix, PrefixCommand.ForExe(Exe));

            plan.IsRunnable.Should().BeTrue(plan.Diagnostic);
            plan.Argv[0].Should().Be(realProton.Combine("proton").ToString());
            plan.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"].Should().Be(realSteam.ToString());
            plan.Environment["STEAM_COMPAT_DATA_PATH"].Should().Be(realCompat.ToString());
        }
        finally
        {
            if (Directory.Exists(rawRoot.ToString())) Directory.Delete(rawRoot.ToString(), recursive: true);
        }
    }

    [Fact]
    public void CallerEnvironmentIsAddedAndWinsOnAClash()
    {
        var env = new Dictionary<string, string>
        {
            ["WINEDEBUG"] = "-all",
            ["STEAM_COMPAT_DATA_PATH"] = "/elsewhere",
        };

        var plan = Runner().Plan(SteamGame(), Exe, [], env);

        plan.Environment["WINEDEBUG"].Should().Be("-all");
        plan.Environment["STEAM_COMPAT_DATA_PATH"].Should().Be("/elsewhere");
        plan.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"].Should().Be(_steam.ToString());
    }

    [Fact]
    public async Task RunWindowsTool_MergesRequiredFilesystemsIntoTheEnvironment()
    {
        var store = _fs.FromUnsanitizedFullPath("/run/media/store").ToString();
        var env = new Dictionary<string, string> { ["PRESSURE_VESSEL_FILESYSTEMS_RW"] = "/already" };

        await Runner([store]).RunWindowsTool(SteamGame(), Exe, [], env, logOutput: false, CancellationToken.None);

        _commands.Should().ContainSingle().Which.EnvironmentVariables["PRESSURE_VESSEL_FILESYSTEMS_RW"]
            .Should().Be($"/already:{store}", "two symlink roots name the store once");
    }

    [Fact]
    public void NoRequiredFilesystemsLeavesThePressureVesselVariableUnset()
    {
        var plan = Runner().Plan(SteamGame(), Exe, [], null);

        plan.Environment.Should().NotContainKey("PRESSURE_VESSEL_FILESYSTEMS_RW");
    }

    [Fact]
    public void HostPathArguments_BecomeWindowsPaths()
    {
        var output = _fs.FromUnsanitizedFullPath("/home/user/out dir");
        var insidePrefix = _prefix.Combine("drive_c/Games/Test Game");

        var plan = Runner().Plan(SteamGame(), Exe, ["-o:plain", output.ToString(), insidePrefix.ToString()], null);

        plan.Argv.Skip(3).Should().Equal("-o:plain", @"Z:\home\user\out dir", @"C:\Games\Test Game");
    }

    [Fact]
    public void NoProtonBuild_CannotRunAndSaysWhy()
    {
        _fs.DeleteDirectory(_protonDir, recursive: true);
        var runner = Runner();

        runner.CanRun(SteamGame(), out var diagnostic).Should().BeFalse();
        diagnostic.Should().NotBeNullOrEmpty();
        diagnostic.Should().Be(new ProtonResolver(new EmptyEnvironment()).ResolveForApp(_steam, 42).Diagnostic);
        ((IToolRunner)runner).CanRun(SteamGame()).Should().BeFalse();
        runner.Plan(SteamGame(), Exe, [], null).Diagnostic.Should().Be(diagnostic);
    }

    [Fact]
    public void SteamGameWithAnInstalledBuild_CanRun()
    {
        Runner().CanRun(SteamGame(), out var diagnostic).Should().BeTrue();
        diagnostic.Should().BeNull();
    }

    [Fact]
    public void SteamGameWithoutTheSteamInstallation_CannotRun()
    {
        Runner().CanRun(SteamGame(withProvider: false), out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("Steam installation");
    }

    [Fact]
    public void GogInPlainWine_GetsTheNoStrategyYetDiagnosticNamingTheKind()
    {
        var runner = Runner();
        var installation = GogInWine();

        var plan = runner.Plan(installation, Exe, [], null);

        plan.IsRunnable.Should().BeFalse();
        plan.Strategy.Should().BeNull();
        plan.Diagnostic.Should().Contain("PlainWine").And.Contain("no strategy yet");
        runner.CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Be(plan.Diagnostic);
    }

    [Fact]
    public void GameWithoutAPrefix_SurfacesTheResolversDiagnostic()
    {
        _fs.DeleteFile(_prefix.Combine("system.reg"));

        var plan = Runner().Plan(SteamGame(), Exe, [], null);

        plan.IsRunnable.Should().BeFalse();
        plan.Diagnostic.Should().Be(new PrefixResolver().Resolve(SteamGame()).Diagnostic);
    }

    [Fact]
    public async Task RunWindowsTool_RunsThePlanThroughTheProcessRunner()
    {
        var result = await Runner().RunWindowsTool(SteamGame(), Exe, ["a b", "-x"], new Dictionary<string, string> { ["FOO"] = "bar" }, logOutput: true, CancellationToken.None);

        result.ExitCode.Should().Be(7);
        var command = _commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be(_protonDir.Combine("proton").ToString());
        command.Arguments.Should().Be($"run {Exe} \"a b\" -x");
        command.WorkingDirPath.Should().Be(Exe.Parent.ToString());
        command.EnvironmentVariables["FOO"].Should().Be("bar");
        command.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"].Should().Be(_prefix.Parent.ToString());
        await _processRunner.Received(1).RunAsync(Arg.Any<Command>(), true, Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>());
    }

    [Fact]
    public async Task RunWindowsTool_RegistersTheToolsPidWhileItRunsAndForgetsItAfter()
    {
        var tools = new StartedTools();
        string? nameWhileRunning = null;
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(call =>
            {
                call.Arg<Action<int>?>()!.Invoke(4242);
                nameWhileRunning = tools.NameOf(4242);
                return new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });

        await Runner(startedTools: tools).RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None);

        nameWhileRunning.Should().Be(Exe.FileName);
        tools.NameOf(4242).Should().BeNull();
    }

    [Fact]
    public async Task RunWindowsTool_ForAGameRegistersNothing()
    {
        var tools = new StartedTools();
        Action<int>? passed = null;
        var invoked = false;
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(call =>
            {
                passed = call.Arg<Action<int>?>();
                passed?.Invoke(4242);
                invoked = passed is not null;
                return new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });

        await Runner(startedTools: tools).RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None, purpose: ToolRunPurpose.Game);

        tools.NameOf(4242).Should().BeNull();
        invoked.Should().BeFalse("a game's pid is found by the detector, not registered as a tool");
    }

    [Fact]
    public async Task RunWindowsTool_ForgetsTheToolsPidWhenTheRunIsCancelled()
    {
        var tools = new StartedTools();
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns<Task<CommandResult>>(call =>
            {
                call.Arg<Action<int>?>()!.Invoke(4242);
                throw new OperationCanceledException();
            });

        var act = () => Runner(startedTools: tools).RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tools.NameOf(4242).Should().BeNull("a pid left registered would mark whatever process reuses it as a tool");
    }

    [Fact]
    public async Task RunWindowsToolReturnsBothTailsTheDurationAndTheRunsPlan()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(async call =>
            {
                var c = call.Arg<Command>();
                await c.StandardOutputPipe.CopyFromAsync(new MemoryStream("out line\n"u8.ToArray()));
                await c.StandardErrorPipe.CopyFromAsync(new MemoryStream("0148:err:module:import_dll Library MSVBVM60.DLL (which is needed by L\"Z:\\\\t\\\\a.exe\") not found\n"u8.ToArray()));
                return new CommandResult(53, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(4));
            });

        var result = await Runner().RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None);

        result.ExitCode.Should().Be(53);
        result.Duration.Should().Be(TimeSpan.FromSeconds(4));
        result.Strategy.Should().Be("SteamProton");
        result.FallbackReason.Should().BeNull();
        result.StdoutTail.Should().Be("out line\n");
        result.StderrTail.Should().Contain("MSVBVM60.DLL");
    }

    [Fact]
    public async Task RunWindowsToolDoesNotReplaceTheLogPipes()
    {
        await Runner().RunWindowsTool(SteamGame(), Exe, [], null, logOutput: true, CancellationToken.None);

        var command = _commands.Should().ContainSingle().Subject;
        command.StandardOutputPipe.Should().NotBeSameAs(PipeTarget.Null);
        command.StandardErrorPipe.Should().NotBeSameAs(PipeTarget.Null);
    }

    [Fact]
    public async Task RunWindowsTool_WithoutAStrategy_ThrowsTheDiagnosticAndRunsNothing()
    {
        var act = () => Runner().RunWindowsTool(GogInWine(), Exe, [], null, logOutput: false, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PlainWine");
        _commands.Should().BeEmpty();
    }

    // An arm64 process whose kernel table has no x86-64 entry, like a headset without FEX registered.
    private HostExecution NoX86_64Interpreter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "smm-binfmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "status"), "enabled\n");
        _cleanup.Add(directory);
        return new HostExecution(directory, Architecture.Arm64);
    }

    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var directory in _cleanup) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void AHostWithoutX86_64RefusesAStrategyThatNeedsIt()
    {
        var runner = Runner(host: NoX86_64Interpreter());

        runner.CanRun(SteamGame(), out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("FEX").And.Contain("HostExecutionSettings.AssumeX86_64Runs").And.Contain("SteamProton");
        runner.Plan(SteamGame(), Exe, [], null).IsRunnable.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedRunThrowsAUserFacingExceptionAndStartsNothing()
    {
        var act = () => Runner(host: NoX86_64Interpreter()).RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None);

        (await act.Should().ThrowAsync<X86_64HostRequiredException>()).Which.Message.Should().Contain("FEX");
        _commands.Should().BeEmpty();
    }

    [Fact]
    public void PlainWineIsNotGatedOnX86_64()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns(_ => new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        var runner = Runner(host: NoX86_64Interpreter(), withPlainWine: true);

        runner.CanRun(GogInWine(), out var diagnostic).Should().BeTrue(diagnostic);
    }

    private sealed class FakeStrategy(bool needsX86_64Host, string? notRunnable) : IToolRunStrategy
    {
        public CompatibilityLayerKind Kind => CompatibilityLayerKind.SteamProton;
        public bool NeedsX86_64Host => needsX86_64Host;
        public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix) => notRunnable;
        public ToolRunPlan Plan(GameInstallation installation, ResolvedPrefix prefix, PrefixCommand command)
            => ToolRunPlan.Runnable("Fake", ["fake"], prefix.Path, new Dictionary<string, string>());
    }

    private sealed class FakeLastResort(bool needsX86_64Host) : ILastResortToolRunStrategy
    {
        public CompatibilityLayerKind Kind => CompatibilityLayerKind.SteamProton;
        public bool NeedsX86_64Host => needsX86_64Host;
        public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix) => null;
        public ToolRunPlan Plan(GameInstallation installation, ResolvedPrefix prefix, PrefixCommand command)
            => ToolRunPlan.Runnable("FakeLastResort", ["fake-last-resort"], prefix.Path, new Dictionary<string, string>());
    }

    private ToolRunner.ToolRunner RunnerOf(HostExecution host, params IToolRunStrategy[] strategies) => RunnerOf(host, [], strategies);

    private ToolRunner.ToolRunner RunnerOf(HostExecution host, IReadOnlyList<string> filesystemRoots, params IToolRunStrategy[] strategies) => new(
        new PrefixResolver(), strategies, _processRunner, RegistryWithMetadata(), LaunchRoots(filesystemRoots), host, UnmanagedGames.Settings(), new StartedTools(), _logger);

    private string[] WarningLogs() => _logger.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning)
        .Select(call => call.GetArguments()[2]!.ToString()!)
        .ToArray();

    private ToolRunner.ToolRunner FallingBackRunner() => RunnerOf(UnmanagedGames.X64Host(), new FakeStrategy(needsX86_64Host: false, notRunnable: "primary cannot run"), new FakeLastResort(needsX86_64Host: false));

    [Fact]
    public void PlanningAFallbackCarriesTheReasonAndLogsNothing()
    {
        var runner = FallingBackRunner();

        var plan = runner.Plan(SteamGame(), Exe, [], null);
        var prefixPlan = runner.PlanInPrefix(SteamGame(), PrefixCommand.ForVerb("regedit"));

        plan.Strategy.Should().Be("FakeLastResort");
        plan.FallbackReason.Should().Be("SteamProton cannot run: primary cannot run");
        prefixPlan.FallbackReason.Should().Be("SteamProton cannot run: primary cannot run");
        WarningLogs().Should().BeEmpty("the Tools page and the doctor report plan without running");
    }

    [Fact]
    public async Task RunningAToolThroughTheLastResortLogsTheRegularStrategysReason()
    {
        await FallingBackRunner().RunWindowsTool(SteamGame(), Exe, [], null, logOutput: false, CancellationToken.None);

        WarningLogs().Should().ContainSingle().Which.Should().Be("Test Game: SteamProton cannot run: primary cannot run; falling back to FakeLastResort");
    }

    [Fact]
    public async Task RunningAPrefixCommandThroughTheLastResortLogsTheReason()
    {
        await FallingBackRunner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("regedit"), CancellationToken.None);

        WarningLogs().Should().ContainSingle().Which.Should().Be("Test Game: SteamProton cannot run: primary cannot run; falling back to FakeLastResort");
    }

    [Fact]
    public void TheFallbackReasonSurvivesLaunchRootMerging()
    {
        var runner = RunnerOf(UnmanagedGames.X64Host(), ["/run/media/store"], new FakeStrategy(needsX86_64Host: false, notRunnable: "primary cannot run"), new FakeLastResort(needsX86_64Host: false));

        var plan = runner.Plan(SteamGame(), Exe, [], null);

        plan.FallbackReason.Should().Be("SteamProton cannot run: primary cannot run");
        plan.Environment.Should().ContainKey("PRESSURE_VESSEL_FILESYSTEMS_RW");
    }

    [Fact]
    public void ARegularStrategyThatRunsHasNoFallbackReason()
    {
        Runner().Plan(SteamGame(), Exe, [], null).FallbackReason.Should().BeNull();
        WarningLogs().Should().BeEmpty();
    }

    [Fact]
    public void AskingWhetherAToolCanRunDoesNotLogTheFallback()
    {
        var runner = RunnerOf(UnmanagedGames.X64Host(), new FakeStrategy(needsX86_64Host: false, notRunnable: "primary cannot run"), new FakeLastResort(needsX86_64Host: false));

        runner.CanRun(SteamGame(), out _).Should().BeTrue();

        WarningLogs().Should().BeEmpty();
    }

    [Fact]
    public void AForcedStrategyThatCannotRunIsFailedWithoutAFallbackReason()
    {
        var plan = Runner().Plan(SteamGame(withProvider: false), Exe, [], null, ToolRunVia.SteamProton);

        plan.IsRunnable.Should().BeFalse();
        plan.Diagnostic.Should().Contain("Steam installation");
        plan.FallbackReason.Should().BeNull();
    }

    [Fact]
    public void AnX86_64LastResortIsRefusedWhenThePrimaryDoesNotNeedX86_64AndCannotRun()
    {
        var runner = RunnerOf(NoX86_64Interpreter(), new FakeStrategy(needsX86_64Host: false, notRunnable: "primary cannot run"), new FakeLastResort(needsX86_64Host: true));

        runner.CanRun(SteamGame(), out var diagnostic, out var cannotRunX86_64).Should().BeFalse();

        cannotRunX86_64.Should().BeTrue();
        diagnostic.Should().Contain("FEX").And.Contain("HostExecutionSettings.AssumeX86_64Runs");
    }

    [Fact]
    public void ALastResortThatDoesNotNeedX86_64RunsOnAnyHost()
    {
        var runner = RunnerOf(NoX86_64Interpreter(), new FakeStrategy(needsX86_64Host: false, notRunnable: "primary cannot run"), new FakeLastResort(needsX86_64Host: false));

        runner.Plan(SteamGame(), Exe, [], null).Strategy.Should().Be("FakeLastResort");
    }

    [Fact]
    public void ARunnablePrimaryThatNeedsNoX86_64IsNotRefusedBecauseALastResortWould()
    {
        var runner = RunnerOf(NoX86_64Interpreter(), new FakeStrategy(needsX86_64Host: false, notRunnable: null), new FakeLastResort(needsX86_64Host: true));

        runner.CanRun(SteamGame(), out var diagnostic, out var cannotRunX86_64).Should().BeTrue(diagnostic);
        cannotRunX86_64.Should().BeFalse();
    }

    [Fact]
    public void AnUnrunnablePrimaryWithNoLastResortKeepsItsOwnReasonOnAnyHost()
    {
        var runner = RunnerOf(NoX86_64Interpreter(), new FakeStrategy(needsX86_64Host: true, notRunnable: "primary cannot run"));

        runner.CanRun(SteamGame(), out var diagnostic, out var cannotRunX86_64).Should().BeFalse();

        diagnostic.Should().Be("primary cannot run");
        cannotRunX86_64.Should().BeFalse();
    }

    [Fact]
    public void TheOverrideSettingSkipsTheGate()
    {
        var runner = Runner(host: NoX86_64Interpreter(), settings: UnmanagedGames.Settings(assumeX86_64Runs: true));

        runner.CanRun(SteamGame(), out var diagnostic).Should().BeTrue(diagnostic);
    }

    [Fact]
    public void AnUnknownBinfmtStateIsRefusedWithTheReadHint()
    {
        var unreadable = new HostExecution("/nonexistent/binfmt_misc", Architecture.Arm64);

        Runner(host: unreadable).CanRun(SteamGame(), out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("could not read /nonexistent/binfmt_misc").And.Contain("HostExecutionSettings.AssumeX86_64Runs")
            .And.NotContain("cannot run x86-64 programs");
    }

    [Fact]
    public void AForcedStrategyIsGatedTheSameWay()
    {
        var plan = Runner(host: NoX86_64Interpreter()).Plan(SteamGame(), Exe, [], null, ToolRunVia.SteamProton);

        plan.IsRunnable.Should().BeFalse();
        plan.Diagnostic.Should().Contain("FEX");
        plan.CannotRunX86_64.Should().BeTrue();
    }

    [Fact]
    public void AnX86_64HostIsNeverAsked()
    {
        var settings = UnmanagedGames.Settings();

        Runner(settings: settings).CanRun(SteamGame(), out _).Should().BeTrue();

        settings.DidNotReceiveWithAnyArgs().Get<HostExecutionSettings>();
    }

    [Fact]
    public void TwoStrategiesForOneKind_AreRefused()
    {
        var protonResolver = new ProtonResolver(new EmptyEnvironment());
        var act = () => new ToolRunner.ToolRunner(
            new PrefixResolver(),
            [new SteamProtonStrategy(protonResolver), new SteamProtonStrategy(protonResolver)],
            _processRunner,
            UnmanagedGames.Registry(),
            UnmanagedGames.LaunchRoots(),
            UnmanagedGames.X64Host(),
            UnmanagedGames.Settings(),
            new StartedTools());

        act.Should().Throw<ArgumentException>().WithMessage("*SteamProton*");
    }

    private sealed class EmptyEnvironment : IEnvironmentReader
    {
        public string? Get(string name) => null;
    }
}
