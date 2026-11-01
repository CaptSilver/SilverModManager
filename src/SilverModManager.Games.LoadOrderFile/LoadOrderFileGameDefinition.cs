using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// What sets one load-order-file game apart from the next: its identity, stores, executable, the folder its mods
/// are laid out in, the file that orders them, and the <see cref="ILoadOrderFileFormat"/> that file is written in.
/// Rows live in <see cref="LoadOrderFileGames"/>; a missing member fails to compile at the row.
/// </summary>
public sealed record LoadOrderFileGameDefinition : IGameDefinition
{
    /// <summary>"LoadOrderFile.&lt;Name&gt;". Loadouts persist it, so it never changes.</summary>
    public required GameId GameId { get; init; }

    public required string DisplayName { get; init; }

    public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }

    public required ImmutableArray<uint> SteamAppIds { get; init; }

    public ImmutableArray<long> GogProductIds { get; init; } = [];

    public StoreIdentifiers Stores => new(GameId) { SteamAppIds = SteamAppIds, GOGProductIds = GogProductIds };

    /// <summary>The executable the game launches, relative to the game folder.</summary>
    public required RelativePath Executable { get; init; }

    /// <summary>The folder below the game folder that holds the mods; a mod is a folder in it.</summary>
    public required RelativePath ModFolder { get; init; }

    /// <summary>How mod archives are laid out, relative to <see cref="ModFolder"/>.</summary>
    public required SimpleInstallRules Install { get; init; }

    /// <summary>The generated file that orders the mods, relative to the game folder.</summary>
    public required RelativePath OrderFile { get; init; }

    public required ILoadOrderFileFormat Format { get; init; }

    /// <summary>Files only this game's install holds, for games that share a store id with another game.</summary>
    public IReadOnlyList<RelativePath> IdentifyingFiles { get; init; } = [];

    /// <summary>Mods the game loads from a folder the app does not manage, kept in the order file when it already names them.</summary>
    public IExternalModSource? ExternalMods { get; init; }

    /// <summary>
    /// Files only a native Linux build of the game holds. Its layout is not the Windows build's, so a loadout for an
    /// install holding one is refused instead of deploying where the game does not read.
    /// </summary>
    public IReadOnlyList<RelativePath> NativeBuildFiles { get; init; } = [];

    /// <summary>Where the mod folder is, as a <see cref="ModLocation"/> the shared installers take.</summary>
    public ModLocation ModRoot => new(ModLocationRoot.GameFolder, ModFolder);

    public RelativePath? NativeLinuxExecutable => null;

    public IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;

    /// <summary>The game folder is the only location; saves are not mapped, so removing a loadout can never touch them.</summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } }.ToImmutableDictionary();
}
