using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.LoadOrderFile.Manifests;
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

    /// <summary>The generated file that orders the mods.</summary>
    public required OrderFileLocation OrderFile { get; init; }

    public required ILoadOrderFileFormat Format { get; init; }

    /// <summary>Files only this game's install holds, for games that share a store id with another game.</summary>
    public IReadOnlyList<RelativePath> IdentifyingFiles { get; init; } = [];

    /// <summary>
    /// Reads a mod's manifest, for games that list a mod by the id in it. The folder it is installed in and the
    /// name the file lists it under then differ.
    /// </summary>
    public IModManifestReader? ManifestReader { get; init; }

    /// <summary>Mods the game loads from folders the app does not install into, each with its own rule for when the order file lists them.</summary>
    public IReadOnlyList<IExternalModSource> ExternalMods { get; init; } = [];

    /// <summary>
    /// Files only a native Linux build of the game holds. Its layout is not the Windows build's, so a loadout for an
    /// install holding one is refused instead of deploying where the game does not read.
    /// </summary>
    public IReadOnlyList<RelativePath> NativeBuildFiles { get; init; } = [];

    /// <summary>
    /// Paths the game writes while it runs (saves, logs), which the loadout must not take for changes. Files that
    /// share the order file's known folder are covered without being listed.
    /// </summary>
    public IReadOnlyList<GamePath> GameWrittenPaths { get; init; } = [];

    /// <summary>
    /// Whether the game reads the order the order file lists mods in. A game that sorts them itself sets this false, so
    /// the Load Order page, which would edit an order the game ignores, is hidden.
    /// </summary>
    public bool FileOrdersLoad { get; init; } = true;

    /// <summary>Where the mod folder is, as a <see cref="ModLocation"/> the shared installers take.</summary>
    public ModLocation ModRoot => new(ModLocationRoot.GameFolder, ModFolder);

    /// <summary>The executable of the game's native Linux build, relative to the game folder; null when it has none.</summary>
    public RelativePath? NativeLinuxExecutable { get; init; }

    /// <summary>What a player calls an extra location, keyed by its location id string.</summary>
    public IReadOnlyDictionary<string, string> LocationNames { get; init; } = ImmutableDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> LocationSentenceNames { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// The game folder, plus the known folder the order file lives in when that is outside it. Saves are not mapped,
    /// so removing a loadout can never touch them.
    /// </summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = ImmutableDictionary.CreateBuilder<LocationId, AbsolutePath>();
        locations.Add(LocationId.Game, gameLocatorResult.Path);
        if (OrderFile.Root != ModLocationRoot.GameFolder)
            locations.Add(SimpleLocations.For(OrderFile.Root), OrderFile.ResolveFolder(fileSystem, gameLocatorResult));
        return locations.ToImmutable();
    }
}
