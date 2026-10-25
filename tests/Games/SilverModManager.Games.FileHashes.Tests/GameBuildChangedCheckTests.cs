using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The note that the store's build is not the one the loadout was last applied to. A sync rewrites the
/// loadout's locator ids from a fresh locate at its start, so comparing them with a fresh locate now
/// says whether the game changed since.
/// </summary>
public sealed class GameBuildChangedCheckTests : IDisposable
{
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly RealQueryEngineHost _host;
    private readonly IConnection _main;
    private readonly GameBuildChangedCheck _check = new();

    public GameBuildChangedCheckTests()
    {
        _host = new RealQueryEngineHost(services => services.AddSingleton(_registry));
        _main = _host.OpenInMemory("main");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Check_StoreBuildDiffersFromTheOneApplied_SuggestsLookingAtMods()
    {
        var diagnostic = Check(await Applied(["a", "b"]), ["a", "c"]).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameBuildChangedMessageData>>().Subject.MessageData;
        data.Game.Should().Be("Test game");
        data.Store.Should().Be("Steam");
        data.WhatBreaks.Should().Be("Mods with a DLL break.");
    }

    [Fact]
    public async Task Check_SameBuildInAnotherOrder_IsSilent()
    {
        Check(await Applied(["a", "b"]), ["b", "a"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_LoadoutNeverApplied_IsSilent()
    {
        // Nothing has been put in the game folder yet, so no mod can have gone stale.
        Check(await NeverApplied(["a"]), ["b"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_StoreReportsNoIds_IsSilent()
    {
        // A locator with no build to compare says nothing about a change.
        Check(await Applied(["a"]), []).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_LoadoutHasNoIds_IsSilent()
    {
        Check(await Applied([]), ["a"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_GameWithoutSensitivity_IsSilent()
    {
        // Games that report their own build drift, such as the FromSoft ones, do not implement
        // IGameUpdateSensitivity, so the shared emitter never hands them to this check.
        var loadout = await Applied(["a"]);
        var emitter = new GameUpdateEmitter([_check], NullLogger<GameUpdateEmitter>.Instance);
        Install(loadout, sensitive: false, fresh: ["b"]);

        var results = new List<Diagnostic>();
        await foreach (var d in emitter.Diagnose(loadout, CancellationToken.None)) results.Add(d);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_SensitiveGameWithNewBuild_YieldsTheNote()
    {
        var loadout = await Applied(["a"]);
        var emitter = new GameUpdateEmitter([_check], NullLogger<GameUpdateEmitter>.Instance);
        Install(loadout, sensitive: true, fresh: ["b"]);

        var results = new List<Diagnostic>();
        await foreach (var d in emitter.Diagnose(loadout, CancellationToken.None)) results.Add(d);

        results.Should().ContainSingle();
    }

    private IEnumerable<Diagnostic> Check(Loadout.ReadOnly loadout, string[] fresh)
    {
        var context = Install(loadout, sensitive: true, fresh);
        return _check.Check(context);
    }

    private Task<Loadout.ReadOnly> Applied(string[] locatorIds) => Add(locatorIds, applied: true);

    private Task<Loadout.ReadOnly> NeverApplied(string[] locatorIds) => Add(locatorIds, applied: false);

    private async Task<Loadout.ReadOnly> Add(string[] locatorIds, bool applied)
    {
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", locatorIds);
        if (applied)
        {
            using var tx = _main.BeginTransaction();
            tx.Add(id, Loadout.LastAppliedDateTime, DateTimeOffset.UtcNow.DateTime);
            await tx.Commit();
        }
        return Loadout.Load(_main.Db, id);
    }

    /// <summary>
    /// Wires a game and a locator that finds <paramref name="fresh"/> ids, and returns the context the
    /// emitter would build. The registry hands back the same installation for the loadout.
    /// </summary>
    private GameUpdateContext Install(Loadout.ReadOnly loadout, bool sensitive, string[] fresh)
    {
        var game = sensitive
            ? Substitute.For<SubstitutableGame, IGameUpdateSensitivity>()
            : Substitute.For<SubstitutableGame>();
        game.DisplayName.Returns("Test game");
        if (sensitive) ((IGameUpdateSensitivity)game).WhatBreaks.Returns("Mods with a DLL break.");

        var locator = Substitute.For<IGameLocator>();
        var result = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..fresh.Select(LocatorId.From)],
            Locator = locator,
        };
        locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>()).Returns(call =>
        {
            call[1] = result;
            return true;
        });

        var installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
        _registry.ForceGetInstallation(Arg.Any<Loadout.ReadOnly>()).Returns(installation);

        return new GameUpdateContext(loadout, installation, sensitive ? (IGameUpdateSensitivity)game : Substitute.For<IGameUpdateSensitivity>(), result);
    }
}
