using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic;

/// <summary>
/// The data a <see cref="DefinedGame{TDefinition}"/> reads its identity, stores and locations from.
/// </summary>
public interface IGameDefinition
{
    GameId GameId { get; }

    string DisplayName { get; }

    Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; }

    StoreIdentifiers Stores { get; }

    /// <summary>The primary file, relative to the game folder.</summary>
    RelativePath Executable { get; }

    /// <summary>The native Linux build's primary file, relative to the game folder; null when there is no native build.</summary>
    RelativePath? NativeLinuxExecutable { get; }

    /// <inheritdoc cref="IGameData.LocationNames"/>
    IReadOnlyDictionary<string, string> LocationNames { get; }

    /// <inheritdoc cref="IGameData.LocationSentenceNames"/>
    IReadOnlyDictionary<string, string> LocationSentenceNames => ImmutableDictionary<string, string>.Empty;

    ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);
}
