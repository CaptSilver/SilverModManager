using System.Collections.Immutable;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Capabilities;

/// <summary>
/// Behaviour a definition can't express as data (writing a plugin list, a launch tool, a synchronizer
/// path filter). Capabilities are C# classes a definition names under <c>capabilities:</c>; the block
/// under that name is the capability's own config.
/// </summary>
public interface IGameCapability
{
    /// <summary>The key a definition uses, camelCase.</summary>
    string Name { get; }

    /// <summary>The type the capability's config block deserialises into.</summary>
    Type ConfigType { get; }

    /// <param name="definition">The definition the capability is attached to.</param>
    /// <param name="config">The config block, an instance of <see cref="ConfigType"/>.</param>
    CapabilityParts Attach(GameDefinition definition, object config, IServiceProvider services);
}

/// <summary>What a capability adds to a game. Everything defaults to nothing.</summary>
public sealed record CapabilityParts
{
    public ImmutableArray<ILibraryItemInstaller> Installers { get; init; } = [];

    /// <summary>Builds the game's synchronizer. At most one capability of a definition may set it.</summary>
    public Func<IGame, ILoadoutSynchronizer>? SynchronizerFactory { get; init; }

    public ImmutableArray<IDiagnosticEmitter> Emitters { get; init; } = [];

    public ImmutableArray<ITool> Tools { get; init; } = [];

    /// <summary>Locations the capability adds beyond the ones the definition declares.</summary>
    public ImmutableDictionary<LocationId, LocationDefinition> ExtraLocations { get; init; } = ImmutableDictionary<LocationId, LocationDefinition>.Empty;

    /// <summary>Files the game itself ships that the synchronizer must not treat as mod files.</summary>
    public ImmutableArray<GamePath> IntrinsicFiles { get; init; } = [];
}
