using System.Collections.Immutable;
using System.Text.RegularExpressions;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Installers;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.Generic.Diagnostics;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.CreationEngine.Abstractions;

/// <summary>
/// Shared shape of a Bethesda title: a data folder, a FOMOD and stop-pattern installer, the usual
/// locations, a synchronizer that leaves saves alone, and the Proton prefix check. A title overrides the
/// literals that differ.
/// </summary>
/// <remarks>
/// Generic because <see cref="IGameData{TSelf}"/> declares the game id, display name and Nexus id as static
/// abstracts; the base forwards its explicit instance members to <typeparamref name="TSelf"/>.
/// Everything built from a virtual member is lazy: a derived constructor body has not run when this
/// constructor does, so reading an overridable member here would see unset state.
/// </remarks>
public abstract class ABethesdaGame<TSelf> : IBethesdaGame, IGameIdentifyingFiles, IGameNativeCode
    where TSelf : ABethesdaGame<TSelf>, IGameData<TSelf>
{
    private readonly Lazy<IStreamFactory> _icon;
    private readonly Lazy<IStreamFactory> _tile;
    private readonly Lazy<ILibraryItemInstaller[]> _installers;
    private readonly Lazy<IDiagnosticEmitter[]> _emitters;
    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    private readonly Lazy<ISortOrderManager> _sortOrderManager;

    protected ABethesdaGame(IServiceProvider provider)
    {
        _icon = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Icon(StoreIdentifiers.SteamAppIds));
        _tile = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Tile(StoreIdentifiers.SteamAppIds));
        _installers = new Lazy<ILibraryItemInstaller[]>(() => CreateInstallers(provider));
        _emitters = new Lazy<IDiagnosticEmitter[]>(() => CreateEmitters(provider).ToArray());
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => CreateSynchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties(CreateSortOrderVarieties(provider), this);
            return sortOrderManager;
        });
    }

    GameId IGameData.GameId => TSelf.GameId;
    string IGameData.DisplayName => TSelf.DisplayName;
    Optional<Sdk.NexusModsApi.NexusModsGameId> IGameData.NexusModsGameId => TSelf.NexusModsGameId;

    public abstract StoreIdentifiers StoreIdentifiers { get; }
    public IStreamFactory IconImage => _icon.Value;
    public IStreamFactory TileImage => _tile.Value;

    public ILibraryItemInstaller[] LibraryItemInstallers => _installers.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters => _emitters.Value;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;

    /// <summary>
    /// Files only this title's install has, for titles that launch the same executable as another one. Most
    /// titles name none.
    /// </summary>
    public virtual IReadOnlyList<RelativePath> IdentifyingFiles => [];

    /// <summary>The executable, relative to the game folder.</summary>
    protected abstract RelativePath PrimaryExe { get; }

    /// <summary>Name of the data folder inside the game folder.</summary>
    protected virtual string DataFolderName => "Data";
    public virtual GamePath DataPath => new(LocationId.Game, DataFolderName);

    /// <summary>Where script extender and other engine files go.</summary>
    protected virtual GamePath EnginePath => new(LocationId.Game, RelativePath.Empty);

    /// <summary>Folder under LocalApplicationData that holds the plugin list, or null when the game has none.</summary>
    protected virtual string? LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => null;

    /// <summary>Folder under My Games that holds INIs and saves, or null when the game has none.</summary>
    protected virtual string? MyGamesFolderName(GameLocatorResult gameLocatorResult) => null;

    /// <summary>Folder names mods use for this game inside their archives.</summary>
    protected abstract string[] GameAliases { get; }
    protected virtual string[] StopPatterns => [];
    protected virtual string[] EngineFiles => [];

    /// <summary>
    /// Where script extender plugins go (Data/SKSE/Plugins), or null for a game without one. Every DLL below it is
    /// built for one game version.
    /// </summary>
    protected virtual GamePath? NativePluginFolder => null;

    private Regex? _engineFilesRegex;

    // Whole-name match, as the installers do: the pattern "skse64_.*\.dll" must not match "myskse64_x.dll".
    private Regex EngineFilesRegex => _engineFilesRegex ??= new Regex(
        $"^(?:{string.Join("|", EngineFiles.Select(p => $"(?:{p})"))})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Extension[] NativeExtensions = [new(".dll"), new(".exe")];

    /// <summary>
    /// The script extender's own DLLs and loader (EngineFiles, in the engine folder) and every DLL in its plugin
    /// folder. The loader's xml and ini files are not code, so the extension check drops them.
    /// </summary>
    public bool HooksGameCode(GamePath path)
    {
        if (NativePluginFolder is { } plugins && NativeCodePaths.IsLibraryBelow(path, plugins)) return true;
        if (EngineFiles.Length == 0) return false;
        return NativeCodePaths.IsDirectlyIn(path, EnginePath, NativeExtensions) && EngineFilesRegex.IsMatch(path.FileName.ToString());
    }
    protected virtual string[] TopLevelDirs => KnownPaths.CommonTopLevelFolders;
    protected virtual Extension[] PluginLikeExtensions => [KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL];
    protected virtual Extension[] ArchiveLikeExtensions => [KnownCEExtensions.BSA, KnownCEExtensions.BA2];

    public virtual IReadOnlyList<GamePath> ProtectedFolders => [new GamePath(LocationId.Preferences, "Saves")];

    public virtual bool IsProtectedPath(GamePath path) => ProtectedFolders.Any(path.InFolder);

    protected virtual ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        FomodXmlInstaller.Create(provider, DataPath),
        CreateStopPatternInstaller(provider),
    ];

    // DataPath and DataFolderName default separately in the installer, and it reads both: the path to place
    // files, the name to strip a leading "Data/" and to spot wrapper folders.
    protected StopPatternInstaller CreateStopPatternInstaller(IServiceProvider provider) => new StopPatternInstaller(provider)
    {
        GameId = TSelf.GameId,
        DataPath = DataPath,
        DataFolderName = DataFolderName,
        EnginePath = EnginePath,
        GameAliases = GameAliases,
        StopPatterns = StopPatterns,
        EngineFiles = EngineFiles,
        TopLevelDirs = TopLevelDirs,
        PluginLikeExtensions = PluginLikeExtensions,
        ArchiveLikeExtensions = ArchiveLikeExtensions,
    }.Build();

    /// <summary>The load orders the Load Order page offers for this title. Most Bethesda titles have none.</summary>
    protected virtual ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [];

    protected virtual IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        provider.GetRequiredService<ProtonPrefixEmitter>(),
    ];

    protected virtual ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new BethesdaSynchronizer(provider, this);

    public virtual Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider) => new();

    public virtual ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } };

        if (LocalAppDataFolderName(gameLocatorResult) is { } appData)
            locations[LocationId.AppData] = fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory) / appData;

        if (MyGamesFolderName(gameLocatorResult) is { } myGames)
            locations[LocationId.Preferences] = fileSystem.GetKnownPath(KnownPath.MyGamesDirectory) / myGames;

        return locations.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, PrimaryExe);

    // Declared here because a default interface member can't be overridden by a subclass that doesn't
    // list the interface itself, and a title may need a different version source.
    public virtual Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            var fvi = installation.Locations.ToAbsolutePath(GetPrimaryFile(installation)).FileInfo.GetFileVersionInfo();
            return fvi.ProductVersion;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }
}
