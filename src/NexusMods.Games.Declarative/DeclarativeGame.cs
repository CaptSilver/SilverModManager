using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Games.Generic.Installers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Games.Declarative;

/// <summary>
/// A game described by a <see cref="GameDefinition"/>. What the definition can't express comes from
/// the capabilities it names, which are attached when the game is created.
/// </summary>
public sealed class DeclarativeGame : IGame
{
    private readonly GameDefinition _definition;
    private readonly ImmutableDictionary<string, LocationDefinition> _locations;
    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    private readonly Lazy<ISortOrderManager> _sortOrderManager;

    /// <exception cref="DefinitionException">The definition's capabilities contradict each other.</exception>
    public DeclarativeGame(IServiceProvider provider, GameDefinition definition)
    {
        _definition = definition;
        GameId = definition.GetGameId();
        StoreIdentifiers = definition.ToStoreIdentifiers();

        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);

        var parts = AttachCapabilities(provider, definition);
        _locations = MergeLocations(definition, parts);

        var synchronizerFactories = parts.Where(p => p.Parts.SynchronizerFactory is not null).ToArray();
        if (synchronizerFactories.Length > 1)
            throw new DefinitionException($"Game '{definition.Id}': capabilities {string.Join(" and ", synchronizerFactories.Select(p => $"'{p.Name}'"))} both supply a synchronizer, only one may");

        var synchronizerFactory = synchronizerFactories.SingleOrDefault().Parts?.SynchronizerFactory;
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => synchronizerFactory is null ? new DefaultSynchronizer(provider) : synchronizerFactory(this));

        // Capabilities first, in the order the definition declares them, then the generic installer.
        // With neither, the install job falls back to the manual installer.
        var installers = parts.SelectMany(p => p.Parts.Installers).ToList();
        if (definition.Installer is { } installer) installers.Add(CreatePatternInstaller(provider, installer));
        LibraryItemInstallers = [..installers];

        var emitters = new List<IDiagnosticEmitter>();
        if (definition.Runtime.Proton != ProtonRequirement.None) emitters.Add(provider.GetRequiredService<ProtonPrefixEmitter>());
        emitters.AddRange(parts.SelectMany(p => p.Parts.Emitters));
        DiagnosticEmitters = [..emitters];

        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties([], this);
            return sortOrderManager;
        });
    }

    public GameId GameId { get; }
    public StoreIdentifiers StoreIdentifiers { get; }
    public string DisplayName => _definition.Name;

    public Optional<NexusModsGameId> NexusModsGameId => _definition.Nexus is { } nexus
        ? Optional<NexusModsGameId>.Create(Sdk.NexusModsApi.NexusModsGameId.From((uint)nexus.Id))
        : Optional<NexusModsGameId>.None;

    public bool HasNativeLinuxBuild => _definition.NativeLinux;
    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = ImmutableDictionary.CreateBuilder<LocationId, AbsolutePath>();
        foreach (var (name, location) in _locations) locations.Add(LocationId.From(name), Resolve(location, fileSystem, gameLocatorResult));
        return locations.ToImmutable();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, _definition.PrimaryExe);

    public Optional<GamePath> GetFallbackCollectionInstallDirectory(GameInstallation installation)
        => _definition.Installer?.Fallback is { } fallback ? ParseTarget(fallback) : Optional<GamePath>.None;

    private static AbsolutePath Resolve(LocationDefinition location, IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var root = location.Root switch
        {
            LocationRoots.Install => gameLocatorResult.Path,
            LocationRoots.LocalAppData => fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory),
            LocationRoots.AppData => fileSystem.GetKnownPath(KnownPath.ApplicationDataDirectory),
            LocationRoots.MyGames => fileSystem.GetKnownPath(KnownPath.MyGamesDirectory),
            LocationRoots.Documents => fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory),
            // Windows keeps Saved Games beside Documents; there is no known path for it.
            LocationRoots.SavedGames => fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory).Parent.Combine("Saved Games"),
            _ => throw new DefinitionException($"Unknown location root '{location.Root}'"),
        };

        return location.Path.Length == 0 ? root : root.Combine(location.Path);
    }

    private static GamePath ParseTarget(string target)
    {
        var parts = target.Split('/', 2);
        return new GamePath(LocationId.From(parts[0]), parts.Length > 1 ? parts[1] : "");
    }

    private static GenericPatternMatchInstaller CreatePatternInstaller(IServiceProvider provider, InstallerDefinition installer) => new(provider)
    {
        InstallFolderTargets =
        [
            new InstallFolderTarget
            {
                DestinationGamePath = ParseTarget(installer.Target),
                KnownSourceFolderNames = installer.DataMarkers.Select(marker => RelativePath.FromUnsanitizedInput(marker)).ToArray(),
            },
        ],
    };

    private static (string Name, CapabilityParts Parts)[] AttachCapabilities(IServiceProvider provider, GameDefinition definition)
    {
        if (definition.Capabilities.Count == 0) return [];

        var registry = provider.GetRequiredService<ICapabilityRegistry>();
        var attached = new List<(string, CapabilityParts)>();
        foreach (var (name, block) in definition.Capabilities)
        {
            if (!registry.TryGet(name, out var capability))
                throw new DefinitionException($"Game '{definition.Id}' names capability '{name}', which is not registered");
            attached.Add((name, capability.Attach(definition, CapabilityConfig.Bind(block, capability.ConfigType), provider)));
        }

        return [..attached];
    }

    private static ImmutableDictionary<string, LocationDefinition> MergeLocations(GameDefinition definition, (string Name, CapabilityParts Parts)[] parts)
    {
        var locations = definition.Locations.ToImmutableDictionary();
        foreach (var (capability, capabilityParts) in parts)
        {
            foreach (var (id, location) in capabilityParts.ExtraLocations)
            {
                var name = id.ToString();
                if (locations.ContainsKey(name))
                    throw new DefinitionException($"Game '{definition.Id}': capability '{capability}' adds location '{name}', which is already declared");
                locations = locations.Add(name, location);
            }
        }

        return locations;
    }
}
