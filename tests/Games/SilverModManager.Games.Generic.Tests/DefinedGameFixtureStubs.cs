using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// A minimal definition-built game for the fixture tests: a game folder with one executable and no location outside
/// it, so locating it needs nothing but the stubbed locator.
/// </summary>
internal sealed record FixtureDefinition : IGameDefinition
{
    public required GameId GameId { get; init; }
    public required string DisplayName { get; init; }
    public Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }
    public StoreIdentifiers Stores => new(GameId);
    public RelativePath Executable => "bin/fixture.exe";
    public RelativePath? NativeLinuxExecutable => null;
    public IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
        ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameLocatorResult.Path);
}

internal sealed class FixtureGame(IServiceProvider provider, FixtureDefinition definition)
    : DefinedGame<FixtureDefinition>(provider, definition)
{
    public static FixtureGame Create(IServiceProvider provider, string id) =>
        new(provider, new FixtureDefinition { GameId = GameId.From(id), DisplayName = id });

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new FixtureSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) => [];
}

internal sealed class FixtureSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider);
