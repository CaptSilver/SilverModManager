using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Steam;

/// <summary>
/// The classification reads a localconfig.vdf built in an in-memory file system; nothing here touches a real
/// Steam folder.
/// </summary>
public class SteamLaunchOptionEmitterTests
{
    private const string AppId = "42";
    private const string Loadout = "Main";
    private const string Shim = "/home/u/.local/share/silvermod/bin/silvermod";

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _localConfig;

    public SteamLaunchOptionEmitterTests()
    {
        _localConfig = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam/userdata/1/config/localconfig.vdf");
    }

    private void WriteLocalConfig(string? launchOptions, string appId = AppId)
    {
        var entry = launchOptions is null ? "" : $"\"LaunchOptions\" \"{launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        _fs.CreateDirectory(_localConfig.Parent);
        _fs.WriteAllText(_localConfig, $$"""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" { "{{appId}}" { {{entry}} } } } } }
            }
            """);
    }

    private GameInstallation Install(GameStore store, string storeId = AppId, bool withProvider = true)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.SteamLocalConfigPath.Returns(_localConfig);

        var folder = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam/steamapps/common/Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = store,
            StoreIdentifier = storeId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            LinuxCompatabilityDataProvider = withProvider ? provider : null,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private static string OurLine(string loadout) => SteamLaunchOptionLine.Compose(Shim, loadout, [], []);

    [Fact]
    public void AGameWithNoLaunchOptionsIsASuggestion()
    {
        WriteLocalConfig(null);

        var finding = SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout);

        finding.Should().NotBeNull();
        finding!.OtherManager.Should().BeNull();
    }

    [Fact]
    public void AnUnrelatedLineIsASuggestion()
    {
        WriteLocalConfig("PROTON_LOG=1 %command% -windowed");

        var finding = SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout);

        finding.Should().NotBeNull();
        finding!.OtherManager.Should().BeNull();
    }

    [Fact]
    public void AnotherManagersLineNamesTheManager()
    {
        WriteLocalConfig("fluorine-manager %command%");

        var finding = SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout);

        finding.Should().NotBeNull();
        finding!.OtherManager.Should().Be("Fluorine");
    }

    [Fact]
    public void OurLineForThisLoadoutIsNotAFinding()
    {
        WriteLocalConfig(OurLine(Loadout));

        SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout).Should().BeNull();
    }

    [Fact]
    public void OurLineForAnotherLoadoutIsASuggestion()
    {
        WriteLocalConfig(OurLine("Other"));

        var finding = SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout);

        finding.Should().NotBeNull();
        finding!.OtherManager.Should().BeNull();
    }

    [Fact]
    public void AGameFromAnotherStoreIsNeverAFinding()
    {
        WriteLocalConfig(null);

        SteamLaunchOptionEmitter.Classify(Install(GameStore.GOG), Loadout).Should().BeNull();
    }

    [Fact]
    public void ASteamGameWithoutKnownSteamDataIsNeverAFinding()
    {
        WriteLocalConfig(null);

        SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam, withProvider: false), Loadout).Should().BeNull();
    }

    [Fact]
    public void ANonNumericAppIdIsNeverAFinding()
    {
        WriteLocalConfig(null);

        SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam, storeId: "not-a-number"), Loadout).Should().BeNull();
    }

    [Fact]
    public void AMissingConfigFileIsNotAFinding()
    {
        SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout).Should().BeNull();
    }

    [Fact]
    public void AnUnparsableConfigFileIsNotAFinding()
    {
        _fs.CreateDirectory(_localConfig.Parent);
        _fs.WriteAllText(_localConfig, "\"UserLocalConfigStore\" { \"Software\" { ");

        SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout).Should().BeNull();
    }

    [Fact]
    public void TheSuggestionOffersTheFixAndListsWhatSteamSkips()
    {
        var diagnostic = SteamLaunchOptionEmitter.Create(Install(GameStore.Steam), Loadout, new SteamLaunchOptionEmitter.Finding(null), "* no DLL overrides");

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        diagnostic.Actions.Should().Equal(DiagnosticActions.UseLoadoutFromSteam());
        diagnostic.Summary.Value.Should().Contain("{GameName}");
        var data = diagnostic.Should().BeOfType<Diagnostic<SteamLaunchDiagnostics.SteamSkipsLoadoutLaunchMessageData>>().Subject.MessageData;
        data.GameName.Should().Be("Test Game");
        data.LoadoutName.Should().Be(Loadout);
        data.Skipped.Should().Contain("no DLL overrides");
    }

    [Fact]
    public void AnotherManagersLineIsAWarningNamingTheManager()
    {
        var diagnostic = SteamLaunchOptionEmitter.Create(Install(GameStore.Steam), Loadout, new SteamLaunchOptionEmitter.Finding("Fluorine"), "* no DLL overrides");

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Actions.Should().Equal(DiagnosticActions.UseLoadoutFromSteam());
        diagnostic.Should().BeOfType<Diagnostic<SteamLaunchDiagnostics.OtherManagerLaunchesGameMessageData>>()
            .Which.MessageData.ManagerName.Should().Be("Fluorine");
    }

    [Fact]
    public void ALauncherThatIsNotKnownByNameIsStillNamed()
    {
        WriteLocalConfig("/opt/thing/run # %command%");

        var finding = SteamLaunchOptionEmitter.Classify(Install(GameStore.Steam), Loadout);

        finding.Should().NotBeNull();
        finding!.OtherManager.Should().Be("/opt/thing/run");
    }
}

public class SteamLaunchSkippedDescriptionTests
{
    private static readonly AbsolutePath GameRoot = new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath("/games/Test");

    private static LaunchSpec Plain => new(GameRoot.Combine("game.exe"), []);

    [Fact]
    public void AGameThatDeclaresNothingListsOnlyTheLoadoutItself()
    {
        var text = SteamLaunchOptionEmitter.DescribeSkipped([], [], Plain, GameRoot.Combine("game.exe"), loadoutNeedsApp: true);

        text.Should().Contain("not applied");
        text.Should().NotContain("DLL");
        text.Should().NotContain("launcher");
        text.Should().NotContain("argument");
    }

    [Fact]
    public void DeclaredOverridesLauncherSkipAndExtraArgumentsAreListedFromTheDeclarations()
    {
        WineDllOverride[] overrides = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var text = SteamLaunchOptionEmitter.DescribeSkipped(overrides, ["--launcher-skip"], new LaunchSpec(GameRoot.Combine("game.exe"), ["-modded"]), GameRoot.Combine("game.exe"), loadoutNeedsApp: true);

        text.Should().Contain("winmm=n,b");
        text.Should().Contain("--launcher-skip");
        text.Should().Contain("-modded");
    }

    [Fact]
    public void TheLoadoutBulletIsLeftOutWhenTheLoadoutDoesNotNeedTheApp()
    {
        var text = SteamLaunchOptionEmitter.DescribeSkipped([], [], new LaunchSpec(GameRoot.Combine("loader.exe"), []), GameRoot.Combine("game.exe"), loadoutNeedsApp: false);

        text.Should().NotContain("not applied");
        text.Should().Contain("loader.exe");
    }

    [Fact]
    public void ARunToolThatStartsAnotherExecutableIsListed()
    {
        var text = SteamLaunchOptionEmitter.DescribeSkipped([], [], new LaunchSpec(GameRoot.Combine("loader.exe"), []), GameRoot.Combine("game.exe"), loadoutNeedsApp: true);

        text.Should().Contain("loader.exe");
    }

    [Fact]
    public void AMissingRunToolListsNoExecutableOrArguments()
    {
        var text = SteamLaunchOptionEmitter.DescribeSkipped([], [], null, GameRoot.Combine("game.exe"), loadoutNeedsApp: true);

        text.Should().NotContain("exe");
    }
}

/// <summary>
/// Whether the diagnostic fires at all: only when the loadout's files or the launch need the app.
/// </summary>
public class SteamLaunchOptionDecisionTests
{
    private static readonly InMemoryFileSystem Fs = new(OSInformation.FakeUnix);
    private static readonly AbsolutePath GameRoot = Fs.FromUnsanitizedFullPath("/games/Test");
    private static readonly AbsolutePath Primary = GameRoot.Combine("game.exe");

    private static readonly DeploymentPlan LinkPlan = new([new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Hardlink)]);
    private static readonly DeploymentPlan FusePlan = new([new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Fuse)]);

    private static readonly SteamLaunchOptionEmitter.Finding NotOurs = new(null);
    private static readonly SteamLaunchOptionEmitter.Finding Fluorine = new("Fluorine");

    private static GameInstallation Install()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = GameRoot,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, GameRoot)));
    }

    private static Diagnostic? Evaluate(SteamLaunchOptionEmitter.Finding finding, DeploymentPlan plan, LaunchSpec? spec, string[]? launcherSkip = null, WineDllOverride[]? overrides = null)
        => SteamLaunchOptionEmitter.Evaluate(Install(), "Main", finding, plan, overrides ?? [], launcherSkip ?? [], spec, Primary);

    private static string SkippedOf(Diagnostic diagnostic) => diagnostic switch
    {
        Diagnostic<SteamLaunchDiagnostics.SteamSkipsLoadoutLaunchMessageData> d => d.MessageData.Skipped,
        Diagnostic<SteamLaunchDiagnostics.OtherManagerLaunchesGameMessageData> d => d.MessageData.Skipped,
        _ => throw new InvalidOperationException("unexpected diagnostic"),
    };

    [Fact]
    public void LinkedFilesWithNothingElseSkippedSayNothing()
    {
        Evaluate(NotOurs, LinkPlan, new LaunchSpec(Primary, [])).Should().BeNull();
    }

    [Fact]
    public void LinkedFilesWithoutARunToolSayNothing()
    {
        Evaluate(NotOurs, LinkPlan, null).Should().BeNull();
    }

    [Fact]
    public void DllOverridesAloneDoNotFireIt()
    {
        Evaluate(NotOurs, LinkPlan, new LaunchSpec(Primary, []), overrides: [new("winmm", [WineDllOverrideType.Native])]).Should().BeNull();
    }

    [Fact]
    public void AnExecutableSubstitutionFiresItWithoutTheLoadoutBullet()
    {
        var diagnostic = Evaluate(NotOurs, LinkPlan, new LaunchSpec(GameRoot.Combine("loader.exe"), []));

        diagnostic.Should().NotBeNull();
        diagnostic!.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        SkippedOf(diagnostic).Should().Contain("loader.exe").And.NotContain("not applied");
    }

    [Fact]
    public void ExtraArgumentsFireItAndKeepTheDllOverridesListed()
    {
        var diagnostic = Evaluate(NotOurs, LinkPlan, new LaunchSpec(Primary, ["-modded"]), overrides: [new("winmm", [WineDllOverrideType.Native])]);

        diagnostic.Should().NotBeNull();
        SkippedOf(diagnostic!).Should().Contain("-modded").And.Contain("winmm").And.NotContain("not applied");
    }

    [Fact]
    public void LauncherSkipArgumentsFireIt()
    {
        var diagnostic = Evaluate(NotOurs, LinkPlan, new LaunchSpec(Primary, []), launcherSkip: ["--skip"]);

        diagnostic.Should().NotBeNull();
        SkippedOf(diagnostic!).Should().Contain("--skip").And.NotContain("not applied");
    }

    [Fact]
    public void AFuseRootFiresItWithTheLoadoutBullet()
    {
        var diagnostic = Evaluate(NotOurs, FusePlan, new LaunchSpec(Primary, []));

        diagnostic.Should().NotBeNull();
        SkippedOf(diagnostic!).Should().Contain("not applied");
    }

    [Fact]
    public void AnOverlayOnlyGameFiresItWithTheLoadoutBullet()
    {
        var diagnostic = Evaluate(NotOurs, LinkPlan, new LaunchSpec(GameRoot.Combine("bwrap"), []) { ReplacesCommand = true, FilesOnlyInLaunch = true });

        diagnostic.Should().NotBeNull();
        SkippedOf(diagnostic!).Should().Contain("not applied");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherManagerIsAlwaysAWarning(bool fuse)
    {
        var diagnostic = Evaluate(Fluorine, fuse ? FusePlan : LinkPlan, new LaunchSpec(Primary, []));

        diagnostic.Should().BeOfType<Diagnostic<SteamLaunchDiagnostics.OtherManagerLaunchesGameMessageData>>()
            .Which.Severity.Should().Be(DiagnosticSeverity.Warning);
        SkippedOf(diagnostic!).Should().NotBeNullOrWhiteSpace();
        SkippedOf(diagnostic!).Contains("not applied").Should().Be(fuse);
    }

    [Fact]
    public void AnotherManagerWithASkippedExecutableLeavesOutTheLoadoutBulletOnLinks()
    {
        var diagnostic = Evaluate(Fluorine, LinkPlan, new LaunchSpec(GameRoot.Combine("loader.exe"), []));

        SkippedOf(diagnostic!).Should().Contain("loader.exe").And.NotContain("not applied");
    }
}
