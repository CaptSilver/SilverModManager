using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.FileHashes.Emitters;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The shared emitter behind the update notes: which games it looks at, and that it reads the store's
/// fields from a fresh locate rather than the result from startup.
/// </summary>
public sealed class GameUpdateEmitterTests : IDisposable
{
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly RealQueryEngineHost _host;
    private readonly IConnection _main;
    private readonly RecordingCheck _check = new();
    private readonly GameUpdateEmitter _emitter;

    public GameUpdateEmitterTests()
    {
        // One emitter for the whole test, as the app registers it as a singleton: a second run must
        // locate again rather than reuse what the first run found.
        _emitter = new GameUpdateEmitter([_check], NullLogger<GameUpdateEmitter>.Instance);
        _host = new RealQueryEngineHost(services => services.AddSingleton(_registry));
        _main = _host.OpenInMemory("main");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Diagnose_GameWithoutSensitivity_RunsNoCheck()
    {
        var loadout = await Install(sensitive: false, startupBuild: "1", locatedBuild: "2");

        await Diagnose(loadout);

        _check.Contexts.Should().BeEmpty();
    }

    [Fact]
    public async Task Diagnose_StoreMovedOnSinceStartup_ChecksSeeTheFreshBuild()
    {
        var loadout = await Install(sensitive: true, startupBuild: "1", locatedBuild: "2");

        await Diagnose(loadout);

        var context = _check.Contexts.Should().ContainSingle().Subject;
        context.Fresh.StoreBuildId.Should().Be("2");
        context.Installation.LocatorResult.StoreBuildId.Should().Be("1", "the cached installation is only for paths");
    }

    [Fact]
    public async Task Diagnose_LocatedAgainOnEveryRun_NotJustTheFirst()
    {
        var loadout = await Install(sensitive: true, startupBuild: "1", locatedBuild: "2");
        await Diagnose(loadout);

        _locatedBuild = "3";
        await Diagnose(loadout);

        _check.Contexts.Select(c => c.Fresh.StoreBuildId).Should().Equal("2", "3");
    }

    [Fact]
    public async Task Diagnose_GameNotLocatedAgain_ChecksGetTheResultFromStartup()
    {
        var loadout = await Install(sensitive: true, startupBuild: "1", locatedBuild: null);

        await Diagnose(loadout);

        _check.Contexts.Should().ContainSingle().Which.Fresh.StoreBuildId.Should().Be("1");
    }

    [Fact]
    public async Task Diagnose_CheckReturnsDiagnostics_YieldsThem()
    {
        var loadout = await Install(sensitive: true, startupBuild: "1", locatedBuild: "1");
        _check.Emit = true;

        (await Diagnose(loadout)).Should().ContainSingle();
    }

    /// <summary>What the locator finds on its next call, or null when it finds nothing.</summary>
    private string? _locatedBuild;

    private async Task<Loadout.ReadOnly> Install(bool sensitive, string startupBuild, string? locatedBuild)
    {
        _locatedBuild = locatedBuild;
        var game = sensitive
            ? Substitute.For<SubstitutableGame, IGameUpdateSensitivity>()
            : Substitute.For<SubstitutableGame>();
        game.DisplayName.Returns("Test game");

        var locator = Substitute.For<IGameLocator>();
        locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>()).Returns(call =>
        {
            // A new result per call, as a real locator reads the manifest again each time.
            call[1] = _locatedBuild is null ? null : Result(game, locator, _locatedBuild);
            return _locatedBuild is not null;
        });

        var startup = Result(game, locator, startupBuild);
        var installation = new GameInstallation(startup, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, startup.Path)));
        _registry.ForceGetInstallation(Arg.Any<Loadout.ReadOnly>()).Returns(installation);

        var id = await HashRows.AddLoadout(_main, GameStore.Steam, startup.Path.ToString());
        return Loadout.Load(_main.Db, id);
    }

    private static GameLocatorResult Result(IGameData game, IGameLocator locator, string buildId) => new()
    {
        Game = game,
        Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
        Store = GameStore.Steam,
        StoreIdentifier = "1",
        LocatorIds = [],
        Locator = locator,
        StoreBuildId = buildId,
    };

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var results = new List<Diagnostic>();
        await foreach (var diagnostic in _emitter.Diagnose(loadout, CancellationToken.None))
            results.Add(diagnostic);
        return results.ToArray();
    }

    private sealed class RecordingCheck : IGameUpdateCheck
    {
        public List<GameUpdateContext> Contexts { get; } = [];
        public bool Emit { get; set; }

        public IEnumerable<Diagnostic> Check(GameUpdateContext context)
        {
            Contexts.Add(context);
            if (Emit) yield return Diagnostics.CreateGameMoved("Test game", "/old", "/new");
        }
    }
}
