using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CliWrap;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Protontricks;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests;

/// <summary>
/// Protontricks is the way to run a program for a Steam game only when no Proton build resolves. The process
/// runner is a fake, so neither protontricks nor flatpak is started.
/// </summary>
public class ProtontricksStrategyTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _prefix;
    private readonly AbsolutePath _protonDir;
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _commands = [];
    private readonly HashSet<string> _installed = [];

    public ProtontricksStrategyTests()
    {
        _steam = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        _prefix = _steam.Combine("steamapps/compatdata/42/pfx");
        _protonDir = _steam.Combine("steamapps/common/Proton 9.0");

        _fs.CreateDirectory(_protonDir);
        _fs.WriteAllText(_protonDir.Combine("proton"), "#!/usr/bin/env python3");
        _fs.CreateDirectory(_prefix);
        _fs.WriteAllText(_prefix.Combine("system.reg"), "");

        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<Command>();
                _commands.Add(command);
                var exitCode = _installed.Contains(command.TargetFilePath) ? 0 : 1;
                return new CommandResult(exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });
    }

    private IToolRunner Runner() => new ToolRunner.ToolRunner(
        new PrefixResolver(),
        [
            new SteamProtonStrategy(new ProtonResolver(new EmptyEnvironment())),
            new ProtontricksStrategy(Protontricks()),
        ],
        _processRunner,
        UnmanagedGames.Registry(),
        UnmanagedGames.LaunchRoots());

    private AggregateProtontricksDependency Protontricks() => new(_processRunner);

    private AbsolutePath Exe => _fs.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    private void NoProtonBuild() => _fs.DeleteDirectory(_protonDir, recursive: true);

    private GameInstallation SteamGame(bool withProvider = true, GameStore? store = null)
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
            Store = store ?? GameStore.Steam,
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

    [Fact]
    public void NoProtonBuild_NativeProtontricks_LaunchesTheProgramInTheAppsPrefix()
    {
        NoProtonBuild();
        _installed.Add("protontricks");

        var plan = Runner().Plan(SteamGame(), Exe, ["-o:plain", "/out/dir"], new Dictionary<string, string> { ["FOO"] = "bar" });

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("Protontricks");
        plan.Argv.Should().Equal("protontricks-launch", "--appid", "42", Exe.ToString(), "-o:plain", @"Z:\out\dir");
        plan.WorkingDirectory.Should().Be(Exe.Parent);
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["FOO"] = "bar" });
    }

    [Fact]
    public void NoProtonBuild_FlatpakProtontricks_LaunchesThroughFlatpak()
    {
        NoProtonBuild();
        _installed.Add("flatpak");

        var plan = Runner().Plan(SteamGame(), Exe, ["-x"], null);

        plan.Strategy.Should().Be("Protontricks");
        plan.Argv.Should().Equal("flatpak", "run", "--command=protontricks-launch", "com.github.Matoking.protontricks", "--appid", "42", Exe.ToString(), "-x");
    }

    [Fact]
    public void NoProtonBuild_ProtontricksIsPreferredToItsFlatpakWhenBothAreInstalled()
    {
        NoProtonBuild();
        _installed.Add("protontricks");
        _installed.Add("flatpak");

        Runner().Plan(SteamGame(), Exe, [], null).Argv[0].Should().Be("protontricks-launch");
    }

    [Fact]
    public void ProtonBuildResolves_ProtontricksIsNotConsulted()
    {
        _installed.Add("protontricks");

        var runner = Runner();
        var plan = runner.Plan(SteamGame(), Exe, [], null);

        plan.Strategy.Should().Be("SteamProton");
        runner.CanRun(SteamGame()).Should().BeTrue();
        _commands.Should().BeEmpty();
    }

    [Fact]
    public void NoProtonBuildAndNoProtontricks_ReportsTheProtonDiagnostic()
    {
        NoProtonBuild();
        var runner = Runner();

        runner.CanRun(SteamGame(), out var diagnostic).Should().BeFalse();

        diagnostic.Should().Be(new ProtonResolver(new EmptyEnvironment()).ResolveForApp(_steam, 42).Diagnostic);
        runner.Plan(SteamGame(), Exe, [], null).Diagnostic.Should().Be(diagnostic);
    }

    [Fact]
    public void SteamInstallationNotFound_ProtontricksStillRunsTheProgram()
    {
        _installed.Add("protontricks");

        var runner = Runner();

        runner.CanRun(SteamGame(withProvider: false)).Should().BeTrue();
        runner.Plan(SteamGame(withProvider: false), Exe, [], null).Strategy.Should().Be("Protontricks");
    }

    [Fact]
    public void NonSteamGame_IsNeverRunWithProtontricks()
    {
        NoProtonBuild();
        _installed.Add("protontricks");
        var installation = SteamGame(store: GameStore.GOG);

        // The prefix kind says Steam, but the store says otherwise, so there is no app to launch in.
        var plan = Runner().Plan(installation, Exe, [], null);

        plan.IsRunnable.Should().BeFalse();
    }

    [Fact]
    public async Task RunWindowsTool_RunsTheProtontricksCommandThroughTheProcessRunner()
    {
        NoProtonBuild();
        _installed.Add("protontricks");
        _commands.Clear();

        var result = await Runner().RunWindowsTool(SteamGame(), Exe, ["a b"], null, logOutput: true, CancellationToken.None);

        result.ExitCode.Should().Be(1, "the fake runner reports unknown programs as failures");
        var command = _commands.Last();
        command.TargetFilePath.Should().Be("protontricks-launch");
        command.Arguments.Should().Be($"--appid 42 {Exe} \"a b\"");
        command.WorkingDirPath.Should().Be(Exe.Parent.ToString());
    }

    private sealed class EmptyEnvironment : IEnvironmentReader
    {
        public string? Get(string name) => null;
    }
}
