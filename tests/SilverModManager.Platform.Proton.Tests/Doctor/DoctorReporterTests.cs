using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Tests.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Doctor;

public class DoctorReporterTests : IDisposable
{
    private const string AppId = "42";

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-doctor-").FullName;
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _prefix;
    private readonly AbsolutePath _localConfig;

    public DoctorReporterTests()
    {
        _steam = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam");
        _prefix = _steam.Combine("steamapps/compatdata/42/pfx");
        _localConfig = _steam.Combine("userdata/1/config/localconfig.vdf");
    }

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private static readonly ProtonBuild Proton9 = new("proton_9", Fixture("/steam/steamapps/common/Proton 9.0"));
    private static readonly ProtonBuild Proton8 = new("proton_8", Fixture("/steam/steamapps/common/Proton 8.0"));

    private static AbsolutePath Fixture(string path) => new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath(path);

    private DoctorReporter Reporter(
        PrefixResolution? prefix = null,
        ProtonResolution? proton = null,
        FakeToolRunner? runner = null,
        params IInstallationCheck[] checks)
        => new(
            new FakePrefixResolver(prefix ?? PrefixResolution.Resolved(new ResolvedPrefix(_prefix, CompatibilityLayerKind.SteamProton, ProtonAppId: 42))),
            new FakeProtonResolver([Proton8, Proton9], proton ?? ProtonResolution.Resolved(Proton9)),
            runner ?? new FakeToolRunner(),
            new InstallationLockFactory(new FakeEnvironment(_runtimeDir)),
            checks);

    private GameInstallation SteamGame(string name = "Test Game", string id = AppId, bool withProvider = true)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        var gameFolder = _steam.Combine("steamapps/common").Combine(name);

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.WinePrefixDirectoryPath.Returns(_prefix);
        provider.SteamInstallPath.Returns(_steam);
        provider.SteamLocalConfigPath.Returns(_localConfig);

        return Install(new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = id,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            LinuxCompatabilityDataProvider = withProvider ? provider : null,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.SteamProton, _prefix),
        });
    }

    private GameInstallation GogInWine()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Wine Game");
        return Install(new GameLocatorResult
        {
            Game = game,
            Path = _fs.FromUnsanitizedFullPath("/wine/drive_c/Wine Game"),
            Store = GameStore.GOG,
            StoreIdentifier = "7",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.PlainWine, _fs.FromUnsanitizedFullPath("/wine")),
        });
    }

    private static GameInstallation Install(GameLocatorResult result)
        => new(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));

    private static ManagedInstallation Managed(GameInstallation installation, params string[] loadouts)
        => new(installation, loadouts.Length == 0 ? ["Main"] : loadouts);

    private void WriteLocalConfig(string? launchOptions)
    {
        var entry = launchOptions is null ? "" : $"\"LaunchOptions\" \"{launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        _fs.CreateDirectory(_localConfig.Parent);
        _fs.WriteAllText(_localConfig, $$"""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" { "{{AppId}}" { {{entry}} } } } } }
            }
            """);
    }

    private string LockPath(string store = "steam", string id = AppId) => InstallationLock.PathFor(_runtimeDir, store, id);

    private sealed class FakeCheck(Func<GameInstallation, IReadOnlyList<DoctorCheck>> check) : IInstallationCheck
    {
        public IReadOnlyList<DoctorCheck> Check(GameInstallation installation) => check(installation);
    }

    [Fact]
    public void Build_InstallationCheckResults_AppearInTheEntryTextAndJson()
    {
        WriteLocalConfig(null);
        var check = new FakeCheck(_ =>
        [
            new DoctorCheck("artifacts", DoctorCheckLevel.Fail, "dinput8.dll is in the game folder"),
            new DoctorCheck("eac", DoctorCheckLevel.Info, "EAC state: armed"),
        ]);

        var report = Reporter(checks: check).Build([Managed(SteamGame())]);

        report.Installations[0].Checks.Should().Equal(
            new DoctorCheck("artifacts", DoctorCheckLevel.Fail, "dinput8.dll is in the game folder"),
            new DoctorCheck("eac", DoctorCheckLevel.Info, "EAC state: armed"));

        var lines = DoctorTextFormatter.Format(report);
        lines.Should().ContainInConsecutiveOrder("  Checks:", "    fail artifacts: dinput8.dll is in the game folder", "    info eac: EAC state: armed");

        var json = JsonDocument.Parse(report.ToJson()).RootElement.GetProperty("installations")[0].GetProperty("checks");
        json.GetArrayLength().Should().Be(2);
        json[0].GetProperty("id").GetString().Should().Be("artifacts");
        json[0].GetProperty("level").GetString().Should().Be("fail");
        json[0].GetProperty("message").GetString().Should().Be("dinput8.dll is in the game folder");
        json[1].GetProperty("level").GetString().Should().Be("info");
    }

    [Fact]
    public void Build_WithoutAContributor_HasAnEmptyChecksArrayAndNoHeading()
    {
        WriteLocalConfig(null);

        var report = Reporter().Build([Managed(SteamGame())]);

        report.Installations[0].Checks.Should().BeEmpty();
        JsonDocument.Parse(report.ToJson()).RootElement.GetProperty("installations")[0].GetProperty("checks").GetArrayLength().Should().Be(0);
        DoctorTextFormatter.Format(report).Should().NotContain(l => l.Contains("Checks"));
    }

    [Fact]
    public void Build_EveryContributorIsAsked_InRegistrationOrder()
    {
        WriteLocalConfig(null);
        var first = new FakeCheck(_ => [new DoctorCheck("a", DoctorCheckLevel.Ok, "one")]);
        var second = new FakeCheck(_ => [new DoctorCheck("b", DoctorCheckLevel.Warn, "two")]);

        var report = Reporter(checks: [first, second]).Build([Managed(SteamGame())]);

        report.Installations[0].Checks.Select(c => c.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void Build_SteamGame_ReportsEveryPart()
    {
        WriteLocalConfig(SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], []));

        var report = Reporter().Build([Managed(SteamGame(), "Main", "Second")]);

        var install = report.Installations.Should().ContainSingle().Subject;
        install.Game.Should().Be("Test Game");
        install.Store.Should().Be("steam");
        install.StoreId.Should().Be(AppId);
        install.Loadouts.Should().Equal("Main", "Second");

        install.Prefix.Should().Be(new DoctorPrefix(DoctorPrefixStatus.Resolved, _prefix.ToString(), CompatibilityLayerKind.SteamProton, null));

        install.Proton.Should().NotBeNull();
        install.Proton!.Builds.Select(b => b.Name).Should().Equal("proton_8", "proton_9");
        install.Proton.Selected.Should().Be(new DoctorProtonBuild("proton_9", Proton9.Directory.ToString()));
        install.Proton.Diagnostic.Should().BeNull();

        install.Runner.CanRun.Should().BeTrue();
        install.Runner.Strategy.Should().Be("SteamProton");
        install.Runner.Argv.Should().Equal(Proton9.Launcher.ToString(), "run", FakeToolRunner.PlaceholderExe);

        install.LaunchOptions.Should().NotBeNull();
        install.LaunchOptions!.ConfigPath.Should().Be(_localConfig.ToString());
        install.LaunchOptions.Status.Should().Be(DoctorLaunchOptionsStatus.Span);
        install.LaunchOptions.Line.Should().Be(SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], []));
        install.LaunchOptions.Loadout.Should().Be("Main");

        install.Lock.Should().Be(new DoctorLock(DoctorLockStatus.Free, null, null, null, null));
    }

    [Fact]
    public void Build_InstallationWithADeploymentPlan_ShowsItInTextAndJson()
    {
        var deployment = new[]
        {
            new DoctorDeploymentRoot("Game", "copy", Recorded: true, Registered: true),
            new DoctorDeploymentRoot("Preferences", "copy", Recorded: false, Registered: true),
            new DoctorDeploymentRoot("Saves/slots", "gone", Recorded: true, Registered: false),
        };
        var report = Reporter().Build([Managed(SteamGame()) with { Deployment = deployment }]);

        report.Installations[0].Deployment.Should().Equal(deployment);
        DoctorTextFormatter.Format(report).Should().Contain("  Deployment: Game -> copy, Preferences -> copy (default), Saves/slots -> gone (no such backend)");

        var json = JsonDocument.Parse(report.ToJson()).RootElement.GetProperty("installations")[0].GetProperty("deployment");
        json.GetArrayLength().Should().Be(3);
        json[2].GetProperty("root").GetString().Should().Be("Saves/slots");
        json[2].GetProperty("registered").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null, null, "Game -> hardlink (chosen)")]
    [InlineData(false, null, "copy", "Game -> hardlink, copy available")]
    [InlineData(false, "same device", null, "Game -> hardlink; reason: same device")]
    [InlineData(true, "picked", "copy", "Game -> hardlink (chosen), copy available; reason: picked")]
    public void Build_DeploymentRootMarkers_AreRenderedInOrder(bool chosen, string? reason, string? available, string expected)
    {
        var deployment = new[] { new DoctorDeploymentRoot("Game", "hardlink", Recorded: true, Registered: true, Chosen: chosen, Reason: reason, Available: available) };

        var report = Reporter().Build([Managed(SteamGame()) with { Deployment = deployment }]);

        DoctorTextFormatter.Format(report).Should().Contain($"  Deployment: {expected}");
        var json = JsonDocument.Parse(report.ToJson()).RootElement.GetProperty("installations")[0].GetProperty("deployment")[0];
        json.GetProperty("chosen").GetBoolean().Should().Be(chosen);
        json.GetProperty("reason").GetString().Should().Be(reason);
        json.GetProperty("available").GetString().Should().Be(available);
    }

    [Fact]
    public void Build_InstallationWithoutADeploymentSource_HasAnEmptyPlanAndNoLine()
    {
        var report = Reporter().Build([Managed(SteamGame())]);

        report.Installations[0].Deployment.Should().BeEmpty();
        DoctorTextFormatter.Format(report).Should().NotContain(l => l.Contains("Deployment:"));
    }

    [Fact]
    public void Build_SamePlacesTwice_GivesTheSameJson_InAnyInputOrder()
    {
        WriteLocalConfig(null);
        var a = Managed(SteamGame("Alpha"));
        var b = Managed(SteamGame("Beta", id: "43"));
        var reporter = Reporter();

        var first = reporter.Build([a, b]).ToJson();
        var second = reporter.Build([b, a]).ToJson();

        second.Should().Be(first);
        reporter.Build([b, a]).Installations.Select(i => i.Game).Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public void Json_RoundTrips()
    {
        WriteLocalConfig(SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], []));
        var report = Reporter().Build([Managed(SteamGame()), Managed(GogInWine())]);

        var json = report.ToJson();
        var back = JsonSerializer.Deserialize<DoctorReport>(json, DoctorReport.JsonOptions);

        back.Should().NotBeNull();
        back!.ToJson().Should().Be(json);
        back.Installations.Should().HaveCount(2);
        json.Should().Contain("\"status\": \"span\"").And.Contain("\"kind\": \"steamProton\"");
    }

    [Fact]
    public void Build_LockHeldByAnotherProcess_ShowsASession()
    {
        WriteLocalConfig(null);
        using var holder = TestHost.StartHolder(LockPath(), seconds: 30);
        try
        {
            var report = Reporter().Build([Managed(SteamGame())]);

            report.Installations[0].Lock.Should().Be(new DoctorLock(DoctorLockStatus.Session, holder.Id, "test-loadout", "running", null));
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void Build_StateFileWithAFreeLock_ShowsLeftover()
    {
        WriteLocalConfig(null);
        using (var holder = TestHost.StartHolder(LockPath(), seconds: 30))
        {
            holder.Kill();
            holder.WaitForExit();
        }

        var report = Reporter().Build([Managed(SteamGame())]);

        var state = report.Installations[0].Lock;
        state.Status.Should().Be(DoctorLockStatus.Leftover);
        state.Loadout.Should().Be("test-loadout");
        state.Phase.Should().Be("running");
        state.Pid.Should().NotBeNull();
    }

    [Fact]
    public void Build_DoesNotLeaveALockHeldOrAStateFileBehind()
    {
        WriteLocalConfig(null);

        Reporter().Build([Managed(SteamGame())]);

        using var taker = new InstallationLock(LockPath());
        // Checked before acquiring: a successful acquire deletes any state file itself.
        File.Exists(taker.StatePath).Should().BeFalse();
        taker.TryAcquire(TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void Build_UnreadableStateFile_IsReportedNotThrown()
    {
        WriteLocalConfig(null);
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath())!);
        File.WriteAllText(Path.ChangeExtension(LockPath(), ".state"), "garbage without equals\n");

        var report = Reporter().Build([Managed(SteamGame())]);

        report.Installations[0].Lock.Status.Should().Be(DoctorLockStatus.Unavailable);
        report.Installations[0].Lock.Diagnostic.Should().Contain("garbage");
    }

    [Fact]
    public void Build_NoRuntimeDir_ReportsTheLockAsUnavailable()
    {
        WriteLocalConfig(null);
        var reporter = new DoctorReporter(
            new FakePrefixResolver(PrefixResolution.Native("native")),
            new FakeProtonResolver([], ProtonResolution.Failed("none")),
            new FakeToolRunner(),
            new InstallationLockFactory(new FakeEnvironment(null)));

        var report = reporter.Build([Managed(SteamGame())]);

        report.Installations[0].Lock.Status.Should().Be(DoctorLockStatus.Unavailable);
        report.Installations[0].Lock.Diagnostic.Should().Contain("XDG_RUNTIME_DIR");
    }

    [Fact]
    public void Build_LaunchOptionsWithoutTheSpan_AreReportedAsSuch()
    {
        WriteLocalConfig("PROTON_LOG=1 %command%");

        var options = Reporter().Build([Managed(SteamGame())]).Installations[0].LaunchOptions;

        options.Should().Be(new DoctorLaunchOptions(_localConfig.ToString(), DoctorLaunchOptionsStatus.NoSpan, "PROTON_LOG=1 %command%", null));
    }

    [Fact]
    public void Build_LocalConfigWithoutAnEntryForTheGame_HasNoOptions()
    {
        WriteLocalConfig(null);

        var options = Reporter().Build([Managed(SteamGame())]).Installations[0].LaunchOptions;

        options.Should().Be(new DoctorLaunchOptions(_localConfig.ToString(), DoctorLaunchOptionsStatus.NoOptions, null, null));
    }

    [Fact]
    public void Build_MissingOrMalformedLocalConfig_IsReportedNotThrown()
    {
        var missing = Reporter().Build([Managed(SteamGame())]).Installations[0].LaunchOptions;
        missing!.Status.Should().Be(DoctorLaunchOptionsStatus.NoConfig);

        _fs.CreateDirectory(_localConfig.Parent);
        _fs.WriteAllText(_localConfig, "\"UserLocalConfigStore\" { \"unterminated");
        var malformed = Reporter().Build([Managed(SteamGame())]).Installations[0].LaunchOptions;
        malformed!.Status.Should().Be(DoctorLaunchOptionsStatus.NoConfig);
    }

    [Fact]
    public void Build_NonSteamGame_HasNoProtonOrLaunchOptionsSection()
    {
        var reporter = Reporter(prefix: PrefixResolution.Resolved(new ResolvedPrefix(_fs.FromUnsanitizedFullPath("/wine"), CompatibilityLayerKind.PlainWine)));

        var install = reporter.Build([Managed(GogInWine())]).Installations.Single();

        install.Store.Should().Be("gog");
        install.Proton.Should().BeNull();
        install.LaunchOptions.Should().BeNull();
        install.Prefix.Kind.Should().Be(CompatibilityLayerKind.PlainWine);
    }

    [Fact]
    public void Build_SteamGameWithoutCompatibilityData_HasNoProtonOrLaunchOptionsSection()
    {
        var install = Reporter().Build([Managed(SteamGame(withProvider: false))]).Installations.Single();

        install.Proton.Should().BeNull();
        install.LaunchOptions.Should().BeNull();
    }

    [Fact]
    public void Build_FailedAndNativePrefixes_CarryTheResolversWords()
    {
        WriteLocalConfig(null);

        var failed = Reporter(prefix: PrefixResolution.Failed("two prefixes")).Build([Managed(SteamGame())]).Installations[0].Prefix;
        failed.Should().Be(new DoctorPrefix(DoctorPrefixStatus.Failed, null, null, "two prefixes"));

        var native = Reporter(prefix: PrefixResolution.Native("runs natively")).Build([Managed(SteamGame())]).Installations[0].Prefix;
        native.Should().Be(new DoctorPrefix(DoctorPrefixStatus.Native, null, null, "runs natively"));
    }

    [Fact]
    public void Build_NoProtonBuildAndNoRunner_CarryTheDiagnostics()
    {
        WriteLocalConfig(null);
        var reporter = Reporter(proton: ProtonResolution.Failed("no proton"), runner: FakeToolRunner.Refusing("nothing can run it"));

        var install = reporter.Build([Managed(SteamGame())]).Installations[0];

        install.Proton!.Selected.Should().BeNull();
        install.Proton.Diagnostic.Should().Be("no proton");
        install.Runner.Should().Be(new DoctorRunner(false, null, [], "nothing can run it"));
    }

    [Fact]
    public void Format_NamesEachSectionAndTheLockHolder()
    {
        WriteLocalConfig(SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], []));
        using var holder = TestHost.StartHolder(LockPath(), seconds: 30);
        try
        {
            var text = string.Join('\n', DoctorTextFormatter.Format(Reporter().Build([Managed(SteamGame())])));

            text.Should().Contain("Test Game").And.Contain("steam 42").And.Contain("Main");
            text.Should().Contain("Prefix").And.Contain(_prefix.ToString());
            text.Should().Contain("Proton").And.Contain("proton_9");
            text.Should().Contain("Runner").And.Contain("SteamProton");
            text.Should().Contain("Launch options").And.Contain("launcher span");
            text.Should().Contain("Launch").And.Contain($"pid {holder.Id}").And.Contain("running");
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void Format_NoInstallations_SaysSo()
    {
        DoctorTextFormatter.Format(new DoctorReport([])).Should().ContainSingle().Which.Should().Contain("No managed games");
    }

    private sealed class FakeEnvironment(string? runtimeDir) : IEnvironmentReader
    {
        public string? Get(string name) => name == "XDG_RUNTIME_DIR" ? runtimeDir : null;
    }

    private sealed class FakePrefixResolver(PrefixResolution result) : IPrefixResolver
    {
        public PrefixResolution Resolve(GameInstallation installation) => result;
    }

    private sealed class FakeProtonResolver(IReadOnlyList<ProtonBuild> builds, ProtonResolution pick) : IProtonResolver
    {
        public IReadOnlyList<ProtonBuild> ListBuilds(AbsolutePath steamInstallPath) => builds;
        public ProtonResolution ResolveForApp(AbsolutePath steamInstallPath, uint appId) => pick;
    }

    private sealed class FakeToolRunner : IToolRunner
    {
        public const string PlaceholderExe = DoctorReporter.PlaceholderExe;

        private readonly string? _refusal;

        public FakeToolRunner() { }

        private FakeToolRunner(string refusal) => _refusal = refusal;

        public static FakeToolRunner Refusing(string reason) => new(reason);

        public ToolRunPlan Plan(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, ToolRunVia via = ToolRunVia.Auto)
            => _refusal is not null
                ? ToolRunPlan.Failed(_refusal)
                : ToolRunPlan.Runnable("SteamProton", [Proton9.Launcher.ToString(), "run", exe.ToString()], exe.Parent, new Dictionary<string, string>());

        public Task<ToolRunResult> RunWindowsTool(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, bool logOutput, CancellationToken cancellationToken, ToolRunVia via = ToolRunVia.Auto)
            => throw new NotSupportedException("the doctor never runs anything");

        public Task<PrefixRunResult> RunInPrefix(GameInstallation installation, PrefixCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException("the doctor never runs anything");

        public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
        {
            diagnostic = _refusal;
            return _refusal is null;
        }
    }
}
