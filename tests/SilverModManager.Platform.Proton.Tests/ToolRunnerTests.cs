using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CliWrap;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests;

public class ToolRunnerTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _prefix;
    private readonly AbsolutePath _protonDir;
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _commands = [];

    public ToolRunnerTests()
    {
        _steam = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        _prefix = _steam.Combine("steamapps/compatdata/42/pfx");
        _protonDir = _steam.Combine("steamapps/common/Proton 9.0");

        _fs.CreateDirectory(_protonDir);
        _fs.WriteAllText(_protonDir.Combine("proton"), "#!/usr/bin/env python3");
        _fs.CreateDirectory(_prefix);
        _fs.WriteAllText(_prefix.Combine("system.reg"), "");

        _processRunner
            .RunAsync(Arg.Do<Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new CommandResult(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private ToolRunner.ToolRunner Runner(IReadOnlyList<string>? filesystemRoots = null)
    {
        var protonResolver = new ProtonResolver(new EmptyEnvironment());
        return new ToolRunner.ToolRunner(
            new PrefixResolver(),
            [new SteamProtonStrategy(protonResolver)],
            _processRunner,
            RegistryWithMetadata(),
            LaunchRoots(filesystemRoots ?? []));
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
        });
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
        await _processRunner.Received(1).RunAsync(Arg.Any<Command>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunWindowsTool_WithoutAStrategy_ThrowsTheDiagnosticAndRunsNothing()
    {
        var act = () => Runner().RunWindowsTool(GogInWine(), Exe, [], null, logOutput: false, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PlainWine");
        _commands.Should().BeEmpty();
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
            UnmanagedGames.LaunchRoots());

        act.Should().Throw<ArgumentException>().WithMessage("*SteamProton*");
    }

    private sealed class EmptyEnvironment : IEnvironmentReader
    {
        public string? Get(string name) => null;
    }
}
