using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Tests.Launch;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Verbs;

public class DoctorCommandTests : IDisposable
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-doctor-command-").FullName;

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Text text) Lines.AddRange(text.Template.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private IEnvironmentReader Environment()
    {
        var environment = Substitute.For<IEnvironmentReader>();
        environment.Get("XDG_RUNTIME_DIR").Returns(_runtimeDir);
        return environment;
    }

    private DoctorCommand Command(params GameLocatorProblem[] problems) => Command(StaleMounts(Substitute.For<IPendingLaunchFinisher>()), problems);

    private static StaleMountSweep StaleMounts(IPendingLaunchFinisher finisher)
    {
        var listed = Substitute.For<IManagedInstallations>();
        listed.List().Returns([]);
        return new StaleMountSweep(listed, finisher);
    }

    private DoctorCommand Command(StaleMountSweep sweep, params GameLocatorProblem[] problems)
    {
        var installations = Substitute.For<IManagedInstallations>();
        installations.List().Returns([]);
        return Command(installations, sweep, problems);
    }

    private DoctorCommand Command(IManagedInstallations installations, StaleMountSweep sweep, params GameLocatorProblem[] problems)
    {
        // Like the real registry, problems exist only once something has located the games.
        var registry = Substitute.For<IGameRegistry>();
        var located = false;
        registry.LocateGameInstallations().Returns(_ =>
        {
            located = true;
            return ImmutableArray<GameInstallation>.Empty;
        });
        registry.Problems.Returns(_ => located ? [..problems] : ImmutableArray<GameLocatorProblem>.Empty);

        var prefixes = Substitute.For<IPrefixResolver>();
        prefixes.Resolve(Arg.Any<GameInstallation>()).Returns(PrefixResolution.Native("native"));
        var reporter = new DoctorReporter(prefixes, Substitute.For<IProtonResolver>(), Substitute.For<IToolRunner>(), new InstallationLockFactory(Environment()));
        return new DoctorCommand(installations, reporter, registry, sweep);
    }

    [Fact]
    public async Task ReleasesAStaleMountBeforeItPrintsTheReport()
    {
        var game = StaleMountSweepTests.Game("Test Game");
        var listed = Substitute.For<IManagedInstallations>();
        listed.List().Returns([new ManagedInstallation(game, [])]);
        var renderer = new RecordingRenderer();
        var order = new List<string>();
        var finisher = Substitute.For<IPendingLaunchFinisher>();
        finisher.FinishIfPending(game, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add(renderer.Lines.Count == 0 ? "sweep before the report" : "sweep after the report");
            return new LaunchFinishReport(LaunchFinishOutcome.NothingPending, game);
        });

        await Command(new StaleMountSweep(listed, finisher)).RunAsync(renderer, json: true);

        order.Should().Equal("sweep before the report");
        renderer.Lines.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PrintsWhatTheSweepDidForEachInstallation()
    {
        var game = StaleMountSweepTests.Game("Test Game");
        var listed = Substitute.For<IManagedInstallations>();
        listed.List().Returns([new ManagedInstallation(game, ["Main"])]);
        var finisher = Substitute.For<IPendingLaunchFinisher>();
        finisher.FinishIfPending(game, Arg.Any<CancellationToken>())
            .Returns(new LaunchFinishReport(LaunchFinishOutcome.Finished, game, "Detached the stale mount at Game/Data"));
        var renderer = new RecordingRenderer();

        await Command(listed, new StaleMountSweep(listed, finisher)).RunAsync(renderer, json: false);

        renderer.Lines.Should().Contain("  Mount sweep: Detached the stale mount at Game/Data");
    }

    [Fact]
    public async Task PrintsEachLocatorProblemOnItsOwnLine()
    {
        var renderer = new RecordingRenderer();

        var exitCode = await Command(
            new GameLocatorProblem("SteamLocator", "Steam app id 489830 is claimed by both 'A' and 'B'"),
            new GameLocatorProblem("HeroicGOGLocator", "GOG product 1 is installed under both roots")).RunAsync(renderer, json: false);

        exitCode.Should().Be(0);
        renderer.Lines.Where(l => l.StartsWith("Locator problem:")).Should().Equal(
            "Locator problem: SteamLocator: Steam app id 489830 is claimed by both 'A' and 'B'",
            "Locator problem: HeroicGOGLocator: GOG product 1 is installed under both roots");
    }

    [Fact]
    public async Task LocatesTheGamesEvenWithNoLoadouts()
    {
        var renderer = new RecordingRenderer();

        await Command(new GameLocatorProblem("SteamLocator", "clash")).RunAsync(renderer, json: false);

        renderer.Lines.Should().Contain("Locator problem: SteamLocator: clash");
    }

    [Fact]
    public async Task PrintsNoProblemLinesWhenTheRegistryHasNone()
    {
        var renderer = new RecordingRenderer();

        await Command().RunAsync(renderer, json: false);

        renderer.Lines.Should().NotContain(l => l.Contains("Locator problem"));
    }

    [Fact]
    public async Task JsonReportCarriesTheProblems()
    {
        var renderer = new RecordingRenderer();

        await Command(new GameLocatorProblem("SteamLocator", "clash")).RunAsync(renderer, json: true);

        using var document = JsonDocument.Parse(string.Concat(renderer.Lines));
        document.RootElement.GetProperty("problems").EnumerateArray().Select(e => e.GetString()).Should().Equal("SteamLocator: clash");
    }
}
