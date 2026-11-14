using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Games;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Ini;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// OpenMW as a game of its own, over the Morrowind install its openmw.cfg points at. Mods deploy into OpenMW's
/// <c>data-local</c> folder, which the engine loads last, so the Morrowind folder is only read and the Morrowind
/// row can manage the same install at the same time. The plugin list is the <c>content=</c> lines of openmw.cfg.
/// </summary>
/// <remarks>
/// The game's folder is the Morrowind root; OpenMW's masters live there, not in the deploy folder, so everything
/// that looks a plugin up goes through <see cref="PluginDirectories"/>.
/// </remarks>
public class OpenMw(IServiceProvider provider) : ACreationEngineGame<OpenMw>(provider), IGameData<OpenMw>
{
    private const string BuiltInScripts = "builtin.omwscripts";

    private readonly IOpenMwConfigRoots _roots = provider.GetRequiredService<IOpenMwConfigRoots>();

    // The one place the two locations are named: the Morrowind row registers the config folder too and reads this.
    internal static readonly IReadOnlyDictionary<string, string> OpenMwLocationNames = new Dictionary<string, string>
    {
        [OpenMwConstants.OpenMwConfig.ToString()] = "OpenMW settings",
        [OpenMwConstants.OpenMwDataLocal.ToString()] = "OpenMW data-local",
    };

    private static readonly IReadOnlyDictionary<string, string> OpenMwLocationSentenceNames = new Dictionary<string, string>
    {
        [OpenMwConstants.OpenMwDataLocal.ToString()] = "the OpenMW data-local folder",
    };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> LocationNames => OpenMwLocationNames;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> LocationSentenceNames => OpenMwLocationSentenceNames;

    public static GameId GameId { get; } = GameId.From("CreationEngine.OpenMW");
    public static string DisplayName => "OpenMW";

    // The same Nexus game as Morrowind: OpenMW mods are listed under it.
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(100);

    // Found through openmw.cfg, not through a store's id.
    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId);

    public override PluginFormat HeaderFormat => PluginFormat.Tes3;
    public override GamePath PluginsFile => OpenMwConstants.OpenMwConfigFile;

    // OpenMW has no masterlist of its own; it shares Morrowind's, but sorts without the master flag.
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.OpenMW;

    private static readonly OpenMwMechanism Mechanism = new();

    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;

    // Same rule as the base: one instance per game, because the variety owns a header cache.
    protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [new OpenMwPluginSortOrderVariety(provider, this)];

    public override IReadOnlyList<string> HardcodedPlugins => [];

    // OpenMW loads plugins in the order of its content= lines and ignores the master flag
    // (libloot plugin/mod.rs is_master_file).
    public override bool MasterFlagIgnored(GameInstallation installation) => true;

    /// <summary>
    /// <c>builtin.omwscripts</c>, which OpenMW always loads first (libloadorder game_settings.rs, early_loading_plugins),
    /// and the game's own masters: the <c>content=</c> names that resolve in a data directory other than data-local
    /// with the master flag set (Morrowind.esm, Tribunal.esm and Bloodmoon.esm on a stock install).
    /// </summary>
    /// <remarks>
    /// Any other <c>content=</c> line the app did not write, such as a plugin the user dropped into the Morrowind
    /// folder by hand, stays where the user put it and is not a row. The <c>content=</c> lines of the config that
    /// loads before the user's (the install's own openmw.cfg) are early as well, whatever their flags: distro
    /// packages can ship some. A config the user's file chains to with <c>config=</c> loads after it, so its content
    /// is not early.
    /// </remarks>
    public override IReadOnlyList<string> EarlyLoadingPlugins(GameInstallation installation)
    {
        var early = new List<string>(base.EarlyLoadingPlugins(installation)) { BuiltInScripts };
        // The install's own config loads before the user's file, so its content comes ahead of the user's masters.
        if (installation.Locations.ContainsKey(OpenMwConstants.OpenMwConfig) && RootOf(installation)?.GlobalConfig is { } global
            && OpenMwPaths.ReadConfig(global) is { } globalText)
            early.AddRange(OpenMwContentWriter.PlainContentNames(globalText));

        if (ReadConfig(installation) is { } config)
        {
            var dataLocal = installation.Locations.ToAbsolutePath(DataPath);
            var elsewhere = PluginDirectories(installation).Where(directory => directory != dataLocal).ToList();
            early.AddRange(OpenMwContentWriter.PlainContentNames(config).Where(name => IsMasterIn(elsewhere, name)));
        }

        return early.Distinct(PluginNameComparer.Instance).ToList();
    }

    private bool IsMasterIn(IReadOnlyList<AbsolutePath> directories, string name)
    {
        if (!ConditionPaths.IsPlainFileName(name) || !PluginExtensions.Contains(RelativePath.FromUnsanitizedInput(name).Extension))
            return false;

        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var file = directories[i] / name;
            if (!file.FileExists) continue;

            // The file is read with the same code that reads every plugin; for a path that exists it finishes at once.
            var header = ParsePluginFile(file, RelativePath.FromUnsanitizedInput(name)).AsTask().GetAwaiter().GetResult();
            return header is not null && PluginFlags.IsMaster(header, masterByExtension: false, masterFlagIgnored: false);
        }

        return false;
    }

    /// <summary>
    /// The folders the engine finds plugins in, lowest priority first: the <c>data=</c> lines of the user's openmw.cfg
    /// in file order, then data-local, which OpenMW loads last.
    /// </summary>
    public override IReadOnlyList<AbsolutePath> PluginDirectories(GameInstallation installation)
    {
        var dataLocal = installation.Locations.ToAbsolutePath(DataPath);
        var directories = new List<AbsolutePath>();
        if (ReadConfig(installation) is { } config && RootOf(installation) is { } root)
        {
            var fileSystem = dataLocal.FileSystem;
            directories.AddRange(OpenMwConfigValues.Values(config, "data")
                .Select(value => OpenMwPaths.Resolve(value, root, fileSystem))
                .OfType<AbsolutePath>());
        }

        directories.RemoveAll(directory => directory == dataLocal);
        directories.Add(dataLocal);
        return directories;
    }

    private string? ReadConfig(GameInstallation installation)
    {
        if (!installation.Locations.ContainsKey(OpenMwConstants.OpenMwConfig)) return null;

        return OpenMwPaths.ReadConfig(installation.Locations.ToAbsolutePath(OpenMwConstants.OpenMwConfigFile));
    }

    private OpenMwConfigRoot? RootOf(GameInstallation installation) =>
        _roots.Roots.FirstOrDefault(root => root.ConfigFolder.GetFullPath() == installation.Locations[OpenMwConstants.OpenMwConfig].Path.GetFullPath());

    // OpenMW's plugin and Lua script formats load like .esp.
    public override Extension[] PluginExtensions =>
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP,
        KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWGame, KnownCEExtensions.OMWScripts,
    ];

    // A .omwscripts file is a text list of Lua scripts, with no header record to read.
    public override ValueTask<PluginHeader?> ParsePlugin(FileIdentity identity, RelativePath? name = null) =>
        name is { } file && file.Extension == KnownCEExtensions.OMWScripts
            ? ValueTask.FromResult<PluginHeader?>(ScriptsHeader(file))
            : base.ParsePlugin(identity, name);

    public override ValueTask<PluginHeader?> ParsePluginFile(AbsolutePath file, RelativePath? name = null) =>
        name is { } plugin && plugin.Extension == KnownCEExtensions.OMWScripts
            ? ValueTask.FromResult<PluginHeader?>(ScriptsHeader(plugin))
            : base.ParsePluginFile(file, name);

    private PluginHeader ScriptsHeader(RelativePath file) => new(file.FileName.ToString(), [], 0, null, null, null, null, HeaderFormat);

    // The file the locator checked: it names the install and has a path of its own, which the Morrowind executable
    // would not for a game that runs through OpenMW.
    protected override RelativePath PrimaryExe => "Data Files/Morrowind.esm";

    // The Morrowind executable's version says nothing about the OpenMW install.
    public override Optional<Version> GetLocalVersion(GameInstallation installation) => Optional<Version>.None;

    // OpenMW starts through its own program, not a file of the game folder.
    public override IReadOnlyList<GamePath> ExecutableFolders(GameInstallation installation) => [];

    /// <inheritdoc />
    protected override string[] SaveExtensions => [".omwsave"];

    /// <inheritdoc />
    protected override string? SaveRedirectUnsupportedReason => "OpenMW keeps its saves in its own user data folder";

    protected override string DataFolderName => "Data Files";

    // Saves live under OpenMW's user data folder, which is not a location.
    public override IReadOnlyList<GamePath> ProtectedFolders => [];

    protected override string[] GameAliases => ["OpenMW", "Morrowind", "MW"];

    protected override string[] TopLevelDirs => [.. KnownPaths.CommonTopLevelFolders, "icons", "bookart", "splash"];

    // OpenMW's plugin and Lua script formats route like .esp.
    protected override Extension[] PluginLikeExtensions =>
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL,
        KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWGame, KnownCEExtensions.OMWScripts,
    ];

    // Morrowind's Ini is the original game's: OpenMW has none to bake keys into.
    protected override IReadOnlyList<IniKeyEdit> ArchiveIniKeys => [];

    /// <summary>
    /// The Morrowind folder is the install's <see cref="LocationId.Game"/>, the config folder is the one the locator
    /// found (it is the store identifier), and the deploy folder is the config's <c>data-local</c>.
    /// </summary>
    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var configFolder = fileSystem.FromUnsanitizedFullPath(gameLocatorResult.StoreIdentifier);
        var root = _roots.Roots.FirstOrDefault(candidate => candidate.ConfigFolder.GetFullPath() == configFolder.GetFullPath())
            ?? throw new InvalidOperationException($"{configFolder} is not one of OpenMW's config folders.");

        var configText = OpenMwPaths.ReadConfig(configFolder / OpenMwConstants.ConfigFileName);

        return base.GetLocations(fileSystem, gameLocatorResult)
            .Add(OpenMwConstants.OpenMwConfig, configFolder)
            .Add(OpenMwConstants.OpenMwDataLocal, OpenMwPaths.DataLocal(configText, root, fileSystem));
    }

    /// <summary>The deploy folder. Mods have no place in the Morrowind install itself.</summary>
    public override GamePath DataPath => new(OpenMwConstants.OpenMwDataLocal, "");

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        new OpenMwConfigEmitter(this),
        .. base.CreateEmitters(provider),
    ];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new OpenMwSynchronizer(provider, this);

    // Only openmw.cfg is managed; settings.cfg, launcher.cfg and the rest of OpenMW's folder are the user's.
    public override bool IsProtectedPath(GamePath path) =>
        base.IsProtectedPath(path)
        || (path.LocationId == OpenMwConstants.OpenMwConfig && path.Path != OpenMwConstants.ConfigFileName);
}
