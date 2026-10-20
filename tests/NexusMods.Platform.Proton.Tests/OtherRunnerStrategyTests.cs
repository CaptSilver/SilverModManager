using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CliWrap;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests;

/// <summary>
/// The strategies for prefixes that Steam does not own: umu and Proton for native Heroic, Heroic's Flatpak
/// sandbox, and plain Wine. Availability is decided by a fake process runner, so nothing is started.
/// </summary>
public class OtherRunnerStrategyTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _probes = [];
    private readonly HashSet<string> _installed = [];
    private readonly HashSet<string> _throwing = [];

    private const string HeroicFlatpakId = "com.heroicgameslauncher.hgl";

    public OtherRunnerStrategyTests()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<Command>();
                _probes.Add(command);
                if (_throwing.Contains(command.TargetFilePath)) throw new Win32Exception(2, "No such file or directory");
                var exitCode = _installed.Contains(command.TargetFilePath) ? 0 : 1;
                return new CommandResult(exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });
    }

    private ToolRunner.ToolRunner Runner() => new(
        new PrefixResolver(),
        [
            new SteamProtonStrategy(new ProtonResolver(new EmptyEnvironment())),
            new UmuStrategy(_processRunner),
            new HeroicFlatpakStrategy(_processRunner),
            new PlainWineStrategy(_processRunner),
        ],
        _processRunner,
        UnmanagedGames.Registry(),
        UnmanagedGames.LaunchRoots());

    private AbsolutePath Exe => _fs.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    private AbsolutePath Path(string path) => _fs.FromUnsanitizedFullPath(path);

    private GameInstallation Install(CompatibilityLayerKind kind, string prefix, string? wineBinary)
    {
        var prefixPath = Path(prefix);
        _fs.CreateDirectory(prefixPath);
        _fs.WriteAllText(prefixPath.Combine("system.reg"), "");

        var binary = wineBinary is null ? (AbsolutePath?)null : Path(wineBinary);
        if (binary is { } bin)
        {
            _fs.CreateDirectory(bin.Parent);
            _fs.WriteAllText(bin, "");
        }

        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = Path("/games/Test Game");

        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.GOG,
            StoreIdentifier = "1234",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            Compatibility = new CompatibilityLayer(kind, prefixPath, binary),
        };

        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    // Heroic keeps Proton runners as <tools>/proton/<name>/proton and Wine runners as <name>/bin/wine.
    private GameInstallation HeroicProton(string prefix = "/heroic/Prefixes/Test Game")
        => Install(CompatibilityLayerKind.Heroic, prefix, "/heroic/tools/proton/GE-Proton9-1/proton");

    // --- plain Wine ---

    [Fact]
    public void PlainWine_RunsTheBinaryTheLocatorRecordedWithTheWinePrefix()
    {
        _installed.Add("/wine/bin/wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", "/wine/bin/wine");

        var plan = Runner().Plan(installation, Exe, ["-IKnowWhatImDoing", "/out/dir"], null);

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("PlainWine");
        plan.Argv.Should().Equal("/wine/bin/wine", Exe.ToString(), "-IKnowWhatImDoing", @"Z:\out\dir");
        plan.WorkingDirectory.Should().Be(Exe.Parent);
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["WINEPREFIX"] = "/wine/prefix" });
    }

    [Fact]
    public void PlainWine_WithoutARecordedBinary_UsesWineFromPath()
    {
        _installed.Add("wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);

        var plan = Runner().Plan(installation, Exe, [], null);

        plan.Argv.Should().Equal("wine", Exe.ToString());
        _probes.Should().ContainSingle().Which.Arguments.Should().Be("--version");
    }

    [Fact]
    public void PlainWine_CallerEnvironmentIsAddedAndWinsOnAClash()
    {
        _installed.Add("wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);

        var plan = Runner().Plan(installation, Exe, [], new Dictionary<string, string> { ["WINEDEBUG"] = "-all", ["WINEPREFIX"] = "/other" });

        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["WINEDEBUG"] = "-all", ["WINEPREFIX"] = "/other" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainWine_WithoutWine_CannotRunAndSaysWhy(bool probeThrows)
    {
        if (probeThrows) _throwing.Add("wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);
        var runner = Runner();

        runner.CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("wine").And.Contain("not installed");
        runner.Plan(installation, Exe, [], null).Diagnostic.Should().Be(diagnostic);
    }

    // --- native Heroic ---

    [Fact]
    public void HeroicProtonRunner_WithUmu_RunsUmuWithTheRunnerAsProtonPath()
    {
        _installed.Add("umu-run");
        var installation = HeroicProton();

        var plan = Runner().Plan(installation, Exe, ["-o:plain", "/out/dir"], null);

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("Umu");
        plan.Argv.Should().Equal("umu-run", Exe.ToString(), "-o:plain", @"Z:\out\dir");
        plan.WorkingDirectory.Should().Be(Exe.Parent);
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["GAMEID"] = "umu-1234",
            ["WINEPREFIX"] = "/heroic/Prefixes/Test Game",
            ["PROTONPATH"] = "/heroic/tools/proton/GE-Proton9-1",
        });
        _probes.Should().ContainSingle().Which.TargetFilePath.Should().Be("umu-run");
    }

    [Fact]
    public void HeroicProtonRunner_CallerEnvironmentWinsOnAClash()
    {
        _installed.Add("umu-run");

        var plan = Runner().Plan(HeroicProton(), Exe, [], new Dictionary<string, string> { ["PROTONPATH"] = "GE-Latest", ["PROTON_LOG"] = "1" });

        plan.Environment["PROTONPATH"].Should().Be("GE-Latest");
        plan.Environment["PROTON_LOG"].Should().Be("1");
        plan.Environment["GAMEID"].Should().Be("umu-1234");
    }

    [Fact]
    public void HeroicProtonRunner_WithoutUmu_RunsTheProtonScriptDirectly()
    {
        var installation = HeroicProton();

        var plan = Runner().Plan(installation, Exe, ["-x"], null);

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("Proton");
        plan.Argv.Should().Equal("/heroic/tools/proton/GE-Proton9-1/proton", "run", Exe.ToString(), "-x");
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = "/heroic/Prefixes/Test Game",
        });
    }

    [Fact]
    public void HeroicProtonRunner_WithoutUmu_UsesTheParentOfAPfxDirectoryAsCompatData()
    {
        var plan = Runner().Plan(HeroicProton("/heroic/Prefixes/Test Game/pfx"), Exe, [], null);

        plan.Environment["STEAM_COMPAT_DATA_PATH"].Should().Be("/heroic/Prefixes/Test Game");
    }

    [Fact]
    public void HeroicProtonRunner_WithoutUmuAndWithoutTheScript_CannotRunAndNamesBoth()
    {
        var installation = HeroicProton();
        _fs.DeleteFile(Path("/heroic/tools/proton/GE-Proton9-1/proton"));
        var runner = Runner();

        runner.CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("umu-run").And.Contain("/heroic/tools/proton/GE-Proton9-1/proton");
        runner.Plan(installation, Exe, [], null).Diagnostic.Should().Be(diagnostic);
    }

    [Fact]
    public void HeroicProtonRunner_CanRunWithUmuAlone()
    {
        _installed.Add("umu-run");
        var installation = HeroicProton();
        _fs.DeleteFile(Path("/heroic/tools/proton/GE-Proton9-1/proton"));

        Runner().CanRun(installation, out var diagnostic).Should().BeTrue();
        diagnostic.Should().BeNull();
    }

    [Fact]
    public void HeroicWineRunner_RunsTheWineBinaryWithoutUmu()
    {
        _installed.Add("/heroic/tools/wine/Wine-GE-8/bin/wine");
        var installation = Install(CompatibilityLayerKind.Heroic, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        var plan = Runner().Plan(installation, Exe, [], null);

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("PlainWine");
        plan.Argv.Should().Equal("/heroic/tools/wine/Wine-GE-8/bin/wine", Exe.ToString());
        plan.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["WINEPREFIX"] = "/heroic/Prefixes/Test Game" });
        _probes.Should().NotContain(c => c.TargetFilePath == "umu-run");
    }

    [Fact]
    public void Heroic_WithoutARecordedRunner_CannotRun()
    {
        var installation = Install(CompatibilityLayerKind.Heroic, "/heroic/Prefixes/Test Game", null);

        Runner().CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("runner");
    }

    // --- Heroic in a Flatpak sandbox ---

    [Fact]
    public void HeroicFlatpak_RunsTheWineBinaryInsideTheSandbox()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        var plan = Runner().Plan(installation, Exe, ["-x", "/out/dir"], new Dictionary<string, string> { ["WINEDEBUG"] = "-all" });

        plan.IsRunnable.Should().BeTrue();
        plan.Strategy.Should().Be("HeroicFlatpak");
        plan.Argv.Should().Equal(
            "flatpak", "run",
            "--command=/heroic/tools/wine/Wine-GE-8/bin/wine",
            "--env=WINEPREFIX=/heroic/Prefixes/Test Game",
            "--env=WINEDEBUG=-all",
            HeroicFlatpakId,
            Exe.ToString(), "-x", @"Z:\out\dir");
        plan.WorkingDirectory.Should().Be(Exe.Parent);
        // The variables travel as --env, so nothing is set on the flatpak process itself.
        plan.Environment.Should().BeEmpty();
        _probes.Should().ContainSingle().Which.Arguments.Should().Be($"info {HeroicFlatpakId}");
    }

    [Fact]
    public void HeroicFlatpak_CallerCannotOverrideTheUnderlyingBinary_ButCanOverrideThePrefix()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        var plan = Runner().Plan(installation, Exe, [], new Dictionary<string, string> { ["WINEPREFIX"] = "/other" });

        plan.Argv.Should().Contain("--env=WINEPREFIX=/other").And.NotContain("--env=WINEPREFIX=/heroic/Prefixes/Test Game");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeroicFlatpak_WithoutFlatpakOrHeroic_CannotRunAndSaysWhy(bool probeThrows)
    {
        if (probeThrows) _throwing.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        Runner().CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("Heroic").And.Contain("flatpak").And.Contain("not installed");
    }

    [Fact]
    public void HeroicFlatpak_WithAProtonRunner_SaysItIsNotSupported()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/proton/GE-Proton9-1/proton");

        Runner().CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("Proton").And.Contain("Flatpak");
    }

    [Fact]
    public void HeroicFlatpak_WithoutARecordedRunner_CannotRun()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", null);

        Runner().CanRun(installation, out var diagnostic).Should().BeFalse();
        diagnostic.Should().Contain("runner");
    }

    // --- whole path through the runner ---

    [Fact]
    public async Task RunWindowsTool_ForPlainWine_StartsWineWithThePrefix()
    {
        _installed.Add("wine");
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);

        var result = await Runner().RunWindowsTool(installation, Exe, ["a b"], null, logOutput: false, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        var command = _probes.Last();
        command.TargetFilePath.Should().Be("wine");
        command.Arguments.Should().Be($"{Exe} \"a b\"");
        command.EnvironmentVariables["WINEPREFIX"].Should().Be("/wine/prefix");
    }

    // --- a forced strategy ---

    [Fact]
    public void ForcedStrategy_OverridesTheOneThePrefixKindPicks()
    {
        _installed.Add("flatpak");
        _installed.Add("/heroic/tools/wine/Wine-GE-8/bin/wine");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        Runner().Plan(installation, Exe, [], null).Strategy.Should().Be("HeroicFlatpak");
        var forced = Runner().Plan(installation, Exe, ["-x"], null, ToolRunVia.Wine);

        forced.IsRunnable.Should().BeTrue();
        forced.Strategy.Should().Be("PlainWine");
        forced.Argv.Should().Equal("/heroic/tools/wine/Wine-GE-8/bin/wine", Exe.ToString(), "-x");
        forced.Environment["WINEPREFIX"].Should().Be("/heroic/Prefixes/Test Game");
    }

    [Fact]
    public void ForcedStrategyThatCannotRun_IsADiagnosticInsteadOfFallingBackToThePrefixPick()
    {
        _installed.Add("flatpak");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        var plan = Runner().Plan(installation, Exe, [], null, ToolRunVia.Wine);

        plan.IsRunnable.Should().BeFalse();
        plan.Strategy.Should().BeNull();
        plan.Diagnostic.Should().Contain("wine").And.Contain("not installed");
    }

    [Fact]
    public async Task ForcedStrategy_IsTheOneRunWindowsToolStarts()
    {
        _installed.Add("flatpak");
        _installed.Add("/heroic/tools/wine/Wine-GE-8/bin/wine");
        var installation = Install(CompatibilityLayerKind.HeroicFlatpak, "/heroic/Prefixes/Test Game", "/heroic/tools/wine/Wine-GE-8/bin/wine");

        await Runner().RunWindowsTool(installation, Exe, [], null, logOutput: false, CancellationToken.None, ToolRunVia.Wine);

        _probes.Last().TargetFilePath.Should().Be("/heroic/tools/wine/Wine-GE-8/bin/wine");
    }

    [Fact]
    public void ForcedNative_IsADiagnosticBecauseTheRunnerOnlyStartsWindowsPrograms()
    {
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);

        var plan = Runner().Plan(installation, Exe, [], null, ToolRunVia.Native);

        plan.IsRunnable.Should().BeFalse();
        plan.Diagnostic.Should().Contain("native");
    }

    [Fact]
    public void ForcedStrategyWithNoRegistration_IsADiagnosticNamingIt()
    {
        var installation = Install(CompatibilityLayerKind.PlainWine, "/wine/prefix", null);
        var runner = new ToolRunner.ToolRunner(new PrefixResolver(), [new PlainWineStrategy(_processRunner)], _processRunner, UnmanagedGames.Registry(), UnmanagedGames.LaunchRoots());

        var plan = runner.Plan(installation, Exe, [], null, ToolRunVia.Umu);

        plan.IsRunnable.Should().BeFalse();
        plan.Diagnostic.Should().Contain("Umu");
    }

    private sealed class EmptyEnvironment : IEnvironmentReader
    {
        public string? Get(string name) => null;
    }
}
