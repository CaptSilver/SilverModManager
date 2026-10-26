using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Protontricks;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests;

/// <summary>
/// Running a Wine verb or an installer in a game's prefix through each strategy. The process runner is a fake
/// that records the command, plays back scripted output into the pipes the runner attached, and never starts
/// a process.
/// </summary>
public class RunInPrefixTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _steamPrefix;
    private readonly AbsolutePath _protonDir;
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly HashSet<string> _installed = [];

    // Only the commands that capture both output streams, not the availability probes.
    private readonly List<Command> _runs = [];
    private CancellationToken _runToken;
    private int _exitCode;
    private string _stdout = "";
    private string _stderr = "";
    private Exception? _runThrows;

    public RunInPrefixTests()
    {
        _steam = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        _steamPrefix = _steam.Combine("steamapps/compatdata/42/pfx");
        _protonDir = _steam.Combine("steamapps/common/Proton 9.0");

        _fs.CreateDirectory(_protonDir);
        _fs.WriteAllText(_protonDir.Combine("proton"), "#!/usr/bin/env python3");
        _fs.CreateDirectory(_steamPrefix);
        _fs.WriteAllText(_steamPrefix.Combine("system.reg"), "");

        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var command = call.Arg<Command>();
                // Availability probes read stdout only; a run captures both streams.
                var isRun = command.StandardErrorPipe != PipeTarget.Null;
                if (!isRun)
                {
                    var probeExit = _installed.Contains(command.TargetFilePath) ? 0 : 1;
                    return new CommandResult(probeExit, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                }

                _runs.Add(command);
                _runToken = call.Arg<CancellationToken>();
                if (_runThrows is not null) throw _runThrows;

                await command.StandardOutputPipe.CopyFromAsync(new MemoryStream(Encoding.UTF8.GetBytes(_stdout)), CancellationToken.None);
                await command.StandardErrorPipe.CopyFromAsync(new MemoryStream(Encoding.UTF8.GetBytes(_stderr)), CancellationToken.None);
                return new CommandResult(_exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });
    }

    private IToolRunner Runner(bool protontricks = false)
    {
        List<IToolRunStrategy> strategies =
        [
            new SteamProtonStrategy(new ProtonResolver(new EmptyEnvironment())),
            new UmuStrategy(_processRunner),
            new HeroicFlatpakStrategy(_processRunner),
            new PlainWineStrategy(_processRunner),
        ];
        if (protontricks) strategies.Add(new ProtontricksStrategy(new AggregateProtontricksDependency(_processRunner)));

        return new ToolRunner.ToolRunner(new PrefixResolver(), strategies, _processRunner, UnmanagedGames.Registry(), UnmanagedGames.LaunchRoots());
    }

    private AbsolutePath Path(string path) => _fs.FromUnsanitizedFullPath(path);

    private AbsolutePath Installer => Path("/tools/vc_redist.x64.exe");

    private GameInstallation SteamGame()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = _steam.Combine("steamapps/common/Test Game");

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.WinePrefixDirectoryPath.Returns(_steamPrefix);
        provider.SteamInstallPath.Returns(_steam);

        return Install(new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            WinePrefixPath = _steamPrefix,
            LinuxCompatabilityDataProvider = provider,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.SteamProton, _steamPrefix),
        });
    }

    private GameInstallation Install(CompatibilityLayerKind kind, string prefix, string? wineBinary)
    {
        var prefixPath = Path(prefix);
        _fs.CreateDirectory(prefixPath);
        _fs.WriteAllText(prefixPath.Combine("system.reg"), "");

        AbsolutePath? binary = wineBinary is null ? null : Path(wineBinary);
        if (binary is { } bin)
        {
            _fs.CreateDirectory(bin.Parent);
            _fs.WriteAllText(bin, "");
        }

        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");

        return Install(new GameLocatorResult
        {
            Game = game,
            Path = Path("/games/Test Game"),
            Store = GameStore.GOG,
            StoreIdentifier = "1234",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            Compatibility = new CompatibilityLayer(kind, prefixPath, binary),
        });
    }

    private GameInstallation Install(GameLocatorResult result)
        => new(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));

    private Command OnlyRun() => _runs.Should().ContainSingle().Subject;

    // --- argv and environment per prefix kind ---

    [Fact]
    public async Task SteamProton_Verb_RunsTheBareVerbThroughTheProtonScript()
    {
        var result = await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("msiexec", ["/i", "setup.msi", "/qn"]), CancellationToken.None);

        result.ExitCode.Should().Be(0);
        var command = OnlyRun();
        command.TargetFilePath.Should().Be(_protonDir.Combine("proton").ToString());
        command.Arguments.Should().Be("run msiexec /i setup.msi /qn");
        command.WorkingDirPath.Should().Be(_steamPrefix.ToString());
        command.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"].Should().Be(_steamPrefix.Parent.ToString());
        command.EnvironmentVariables["STEAM_COMPAT_CLIENT_INSTALL_PATH"].Should().Be(_steam.ToString());
    }

    [Fact]
    public async Task SteamProton_Exe_RunsTheExeWithItsHostPathAndTranslatedArguments()
    {
        await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForExe(Installer, ["/install", "-o:plain", "/out/dir"]), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be(_protonDir.Combine("proton").ToString());
        command.Arguments.Should().Be($"run {Installer} Z:\\install -o:plain Z:\\out\\dir");
        command.WorkingDirPath.Should().Be(Installer.Parent.ToString());
    }

    [Fact]
    public async Task Verb_ArgumentsAreNotRewrittenAsHostPaths()
    {
        // msiexec and regedit take /switches, which the path translator would turn into Z:\ paths.
        await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("regedit", ["/S", @"C:\backup.reg"]), CancellationToken.None);

        OnlyRun().Arguments.Should().Be(@"run regedit /S C:\backup.reg");
    }

    [Fact]
    public async Task SteamProton_ExtraEnvironmentIsMergedOverTheStrategysOwn()
    {
        var env = new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "mshtml=d", ["STEAM_COMPAT_DATA_PATH"] = "/elsewhere" };

        await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("wineboot", ["-u"], env), CancellationToken.None);

        var vars = OnlyRun().EnvironmentVariables;
        vars["WINEDLLOVERRIDES"].Should().Be("mshtml=d");
        vars["STEAM_COMPAT_DATA_PATH"].Should().Be("/elsewhere");
        vars["STEAM_COMPAT_CLIENT_INSTALL_PATH"].Should().Be(_steam.ToString());
    }

    [Fact]
    public async Task PlainWine_Verb_RunsWineWithTheVerbAndThePrefix()
    {
        _installed.Add("/wine/bin/wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", "/wine/bin/wine");

        await Runner().RunInPrefix(installation, PrefixCommand.ForVerb("msiexec", ["/i", "setup.msi"], new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "mshtml=d" }), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("/wine/bin/wine");
        command.Arguments.Should().Be("msiexec /i setup.msi");
        command.WorkingDirPath.Should().Be("/wine/prefix");
        command.EnvironmentVariables.Should().BeEquivalentTo(new Dictionary<string, string?> { ["WINEPREFIX"] = "/wine/prefix", ["WINEDLLOVERRIDES"] = "mshtml=d" });
    }

    [Fact]
    public async Task PlainWine_Exe_RunsWineWithTheExe()
    {
        _installed.Add("/wine/bin/wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", "/wine/bin/wine");

        await Runner().RunInPrefix(installation, PrefixCommand.ForExe(Installer, ["/out/dir"]), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("/wine/bin/wine");
        command.Arguments.Should().Be($"{Installer} Z:\\out\\dir");
        command.WorkingDirPath.Should().Be(Installer.Parent.ToString());
    }

    [Fact]
    public async Task HeroicProton_WithUmu_Verb_RunsUmuWithTheVerb()
    {
        _installed.Add("umu-run");
        var installation = Install(CompatibilityLayerKind.Heroic, "/heroic/Prefixes/Game", "/heroic/tools/proton/GE-Proton9-1/proton");

        await Runner().RunInPrefix(installation, PrefixCommand.ForVerb("wineboot", ["-u"]), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("umu-run");
        command.Arguments.Should().Be("wineboot -u");
        command.EnvironmentVariables["WINEPREFIX"].Should().Be("/heroic/Prefixes/Game");
        command.EnvironmentVariables["PROTONPATH"].Should().Be("/heroic/tools/proton/GE-Proton9-1");
        command.EnvironmentVariables["GAMEID"].Should().Be("umu-1234");
    }

    [Fact]
    public async Task HeroicProton_WithoutUmu_Verb_RunsTheProtonScript()
    {
        var installation = Install(CompatibilityLayerKind.Heroic, "/heroic/Prefixes/Game/pfx", "/heroic/tools/proton/GE-Proton9-1/proton");

        await Runner().RunInPrefix(installation, PrefixCommand.ForVerb("regedit", ["/S", "x.reg"]), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("/heroic/tools/proton/GE-Proton9-1/proton");
        command.Arguments.Should().Be("run regedit /S x.reg");
        command.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"].Should().Be("/heroic/Prefixes/Game");
    }

    [Fact]
    public async Task HeroicFlatpak_Verb_RunsTheVerbInsideTheSandboxWithEveryVariableAsEnv()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        await Runner().RunInPrefix(installation, PrefixCommand.ForVerb("msiexec", ["/i", "setup.msi"], new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "mshtml=d" }), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("flatpak");
        command.Arguments.Should().Be("run --command=/heroic/tools/wine/Wine-GE-8/bin/wine --env=WINEPREFIX=/heroic/Prefixes/Game --env=WINEDLLOVERRIDES=mshtml=d com.heroicgameslauncher.hgl msiexec /i setup.msi");
    }

    // --- Protontricks ---

    [Fact]
    public async Task Protontricks_Exe_LaunchesTheExeInTheAppsPrefix()
    {
        _fs.DeleteDirectory(_protonDir, recursive: true);
        _installed.Add("protontricks");

        await Runner(protontricks: true).RunInPrefix(SteamGame(), PrefixCommand.ForExe(Installer, ["/out/dir"], new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "mshtml=d" }), CancellationToken.None);

        var command = OnlyRun();
        command.TargetFilePath.Should().Be("protontricks-launch");
        command.Arguments.Should().Be($"--appid 42 {Installer} Z:\\out\\dir");
        command.EnvironmentVariables["WINEDLLOVERRIDES"].Should().Be("mshtml=d");
    }

    [Fact]
    public async Task Protontricks_Verb_IsRefusedBecauseTheLauncherTakesOnlyAnExecutable()
    {
        _fs.DeleteDirectory(_protonDir, recursive: true);
        _installed.Add("protontricks");

        var act = () => Runner(protontricks: true).RunInPrefix(SteamGame(), PrefixCommand.ForVerb("msiexec", ["/i", "setup.msi"]), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("protontricks").And.Contain("msiexec");
        _runs.Should().BeEmpty();
    }

    // --- results, failures, cancellation ---

    [Fact]
    public async Task ExitCodeAndBothOutputStreamsAreReturned()
    {
        _exitCode = 3010;
        _stdout = "installed\nrestart needed\n";
        _stderr = "fixme:msi:something\n";

        var result = await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForExe(Installer, ["/quiet"]), CancellationToken.None);

        result.Should().Be(new PrefixRunResult(3010, "installed\nrestart needed\n", "fixme:msi:something\n"));
    }

    [Fact]
    public async Task ANonZeroExitCodeIsAResultNotAnException()
    {
        _exitCode = 1603;

        var result = await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("msiexec", ["/i", "x.msi"]), CancellationToken.None);

        result.ExitCode.Should().Be(1603);
    }

    [Fact]
    public async Task CancellationTokenReachesTheProcessRunnerAndCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        _runThrows = new OperationCanceledException(cts.Token);
        await cts.CancelAsync();

        var act = () => Runner().RunInPrefix(SteamGame(), PrefixCommand.ForVerb("wineboot", ["-u"]), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _runToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task WhenNothingCanRunTheProgram_TheDiagnosticIsThrown()
    {
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);

        var act = () => Runner().RunInPrefix(installation, PrefixCommand.ForVerb("wineboot", ["-u"]), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("wine").And.Contain("not installed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("msi exec")]
    [InlineData("/usr/bin/wine")]
    [InlineData(@"C:\windows\regedit.exe")]
    public void AVerbMustBeABareProgramName(string verb)
    {
        var act = () => PrefixCommand.ForVerb(verb, []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ExtraEnvironmentIsAlsoMergedForExecutables()
    {
        await Runner().RunInPrefix(SteamGame(), PrefixCommand.ForExe(Installer, [], new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "mshtml=d" }), CancellationToken.None);

        OnlyRun().EnvironmentVariables["WINEDLLOVERRIDES"].Should().Be("mshtml=d");
    }

    private sealed class EmptyEnvironment : IEnvironmentReader
    {
        public string? Get(string name) => null;
    }
}
