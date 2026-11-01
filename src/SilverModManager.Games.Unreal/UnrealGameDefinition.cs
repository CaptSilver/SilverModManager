using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Games.Generic;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// What sets one Unreal Engine game apart from the next: its identity, stores, the launcher in the game folder and
/// the name of the Unreal project folder its paks live under. Rows live in <see cref="UnrealGames"/>; a missing
/// member fails to compile at the row.
/// </summary>
public sealed record UnrealGameDefinition : IGameDefinition
{
    /// <summary>"Unreal.&lt;Name&gt;". Loadouts persist it, so it never changes.</summary>
    public required GameId GameId { get; init; }

    public required string DisplayName { get; init; }

    public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }

    /// <summary>The executable Steam launches, relative to the game folder. Usually a launcher beside the project folder.</summary>
    public required RelativePath Executable { get; init; }

    /// <summary>The Unreal project folder under the game folder, the one holding Content and Binaries.</summary>
    public required RelativePath ProjectFolder { get; init; }

    public required ImmutableArray<uint> SteamAppIds { get; init; }

    public ImmutableArray<long> GogProductIds { get; init; } = [];

    public StoreIdentifiers Stores => new(GameId) { SteamAppIds = SteamAppIds, GOGProductIds = GogProductIds };

    /// <summary>The folder under <c>Binaries</c> that holds the shipping binary.</summary>
    public string BinariesPlatform { get; init; } = "Win64";

    public RelativePath? NativeLinuxExecutable => null;

    public IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;

    /// <summary>The paks, LogicMods and UE4SS folders of this game's project.</summary>
    public UnrealModLayout Layout
    {
        get
        {
            var project = new GamePath(LocationId.Game, ProjectFolder);
            return new UnrealModLayout(project / "Content", project / $"Binaries/{BinariesPlatform}");
        }
    }

    /// <summary>The game folder is the only location; saves are not mapped, so removing a loadout can never touch them.</summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } }.ToImmutableDictionary();
}
