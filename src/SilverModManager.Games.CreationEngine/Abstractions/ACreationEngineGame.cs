using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.CreationEngine.Modlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Ordering;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Abstractions;

/// <summary>
/// A Bethesda title whose plugin headers the load order reader can parse: adds the Plugins.txt intrinsic file and the
/// missing-master check on top of <see cref="ABethesdaGame{TSelf}"/>.
/// </summary>
public abstract class ACreationEngineGame<TSelf> : ABethesdaGame<TSelf>, ICreationEngineGame, IMo2InstanceTarget, IGameIniFiles
    where TSelf : ACreationEngineGame<TSelf>, IGameData<TSelf>
{
    private readonly IStreamSourceDispatcher _streamSource;
    private readonly IServiceProvider _provider;

    protected ACreationEngineGame(IServiceProvider provider) : base(provider)
    {
        _provider = provider;
        _streamSource = provider.GetRequiredService<IStreamSourceDispatcher>();
    }

    public abstract PluginFormat HeaderFormat { get; }

    public abstract GamePath PluginsFile { get; }

    public abstract LootGame LootGame(GameInstallation installation);
    public abstract IPluginListMechanism PluginList(GameInstallation installation);

    // libloadorder game_settings.rs: hardcoded_plugins.
    public abstract IReadOnlyList<string> HardcodedPlugins { get; }

    public virtual GamePath? CreationClubListFile => null;

    /// <summary>A list file the game reads only when <see cref="CreationClubListFile"/> is absent from disk.</summary>
    protected virtual GamePath? FallbackCreationClubListFile => null;

    /// <summary>Overridden by the games that keep a record of the content they install themselves.</summary>
    protected virtual IGameInstalledContentRecognizer? GameInstalledContent => null;

    IGameInstalledContentRecognizer? ICreationEngineGame.GameInstalledContent => GameInstalledContent;

    // Not on ABethesdaGame: it is the base class and cannot see GameInstalledContent. The game's records are
    // read by the recognizer only; the sync neither indexes nor reports them.
    public override bool IsProtectedPath(GamePath path) =>
        base.IsProtectedPath(path)
        || (GameInstalledContent?.RecordPaths.Any(record => path == record || path.InFolder(record)) ?? false);

    public virtual IReadOnlyList<GamePath> TestFileIniFiles => [];

    public virtual Extension[] PluginExtensions => SupportsLightPlugins
        ? [KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL]
        : [KnownCEExtensions.ESM, KnownCEExtensions.ESP];

    public virtual bool SupportsLightPlugins => false;
    public virtual bool MasterByExtension => false;
    public virtual bool MasterFlagIgnored(GameInstallation installation) => false;

    public virtual IReadOnlyList<string> ImplicitlyActivePlugins(IEnumerable<string> dataFileNames) => [];

    /// <summary>Only Starfield lays its flag bits out differently, and needs the medium, update and blueprint kinds.</summary>
    protected virtual bool IsStarfield => false;

    public bool IsMaster(PluginHeader header, GameInstallation installation) =>
        PluginFlags.IsMaster(header, MasterByExtension, MasterFlagIgnored(installation));

    public bool IsLight(PluginHeader header) => PluginFlags.IsLight(header, SupportsLightPlugins, IsStarfield);
    public bool IsMedium(PluginHeader header) => PluginFlags.IsMedium(header, SupportsLightPlugins, IsStarfield);
    public bool IsUpdate(PluginHeader header) => PluginFlags.IsUpdate(header, IsStarfield);
    public bool IsBlueprint(PluginHeader header) => PluginFlags.IsBlueprint(header, IsStarfield);

    public virtual IReadOnlyList<string> EarlyLoadingPlugins(GameInstallation installation)
    {
        var names = new List<string>(HardcodedPlugins);
        foreach (var candidate in new[] { CreationClubListFile, FallbackCreationClubListFile })
        {
            if (candidate is not { } path || !installation.Locations.ContainsKey(path.LocationId)) continue;

            var file = installation.Locations.ToAbsolutePath(path);
            if (!file.FileExists) continue;

            // The first file that exists wins even when it is empty: Starfield's My Games list overrides the game folder's.
            using var reader = new StreamReader(file.Read());
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length > 0) names.Add(line);
            }
            break;
        }

        return names.Distinct(PluginNameComparer.Instance).ToList();
    }

    /// <summary>
    /// For a game that orders its plugins by file time: the plugins in load order, the same order the plugin list
    /// is written in, each with a time later than the one before. The plugins' own times are reused where they
    /// can be (<see cref="TimestampAssigner"/>), so files keep roughly the dates they had.
    /// </summary>
    /// <remarks>
    /// A file that is neither on disk nor about to be extracted has no time to give, and is left out.
    /// </remarks>
    protected async Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PlanPluginTimestamps(
        Loadout.ReadOnly loadout,
        IReadOnlyDictionary<GamePath, SyncNode> syncTree)
    {
        var file = PluginList(loadout.InstallationInstance).IntrinsicFiles(this, loadout, _provider).OfType<PluginsFile>().First();
        var ordered = new List<(GamePath Path, SyncNode Node)>();
        foreach (var name in await file.LoadOrder(loadout, syncTree))
        {
            var path = DataPath / name;
            if (syncTree.TryGetValue(path, out var node) && (node.HaveDisk || node.Actions.HasFlag(Actions.ExtractToDisk)))
                ordered.Add((path, node));
        }

        // A file about to be replaced has no time worth keeping.
        var existing = ordered
            .Where(o => o.Node.HaveDisk && !o.Node.Actions.HasFlag(Actions.ExtractToDisk))
            .Select(o => new DateTimeOffset(o.Node.Disk.LastModifiedTicks, TimeSpan.Zero))
            .ToList();
        var times = TimestampAssigner.Assign(existing, ordered.Count);
        return ordered.Select((o, i) => (o.Path, times[i])).ToList();
    }

    public virtual async ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null)
    {
        await using var stream = await _streamSource.OpenAsync(hash);
        if (stream is null) return null;

        return PluginHeaderReader.Read(stream, HeaderFormat, name?.FileName.ToString() ?? "unknown.esm");
    }

    string? IMo2InstanceTarget.UnsupportedReason(GameInstallation installation) => CreationEngineMo2Target.UnsupportedReason(TSelf.GameId);

    GamePath IMo2InstanceTarget.ModFilePath(GameInstallation installation, RelativePath modRelative) => CreationEngineMo2Target.ModFilePath(this, modRelative);

    IReadOnlyList<GamePath> IMo2InstanceTarget.ProfileSettingsFiles(GameInstallation installation) => CreationEngineMo2Target.ProfileSettingsFiles(TSelf.GameId);

    bool IMo2InstanceTarget.IsGameRootFolder(GameInstallation installation, string folderName, IReadOnlySet<RelativePath> filesDirectlyInside) =>
        CreationEngineMo2Target.IsGameRootFolder(this, installation, filesDirectlyInside);

    void IMo2InstanceTarget.StagePluginList(Loadout.ReadOnly loadout, SortOrderId sortOrderId, string? pluginsTxt, string? loadorderTxt, ITransaction tx) =>
        CreationEngineMo2Target.StagePluginList(
            this,
            SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single(),
            loadout, sortOrderId, pluginsTxt, loadorderTxt, tx);

    // One instance per game: it owns a header cache, so it is never a shared service.
    protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [new PluginSortOrderVariety(provider, this)];

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider)
    {
        yield return new MissingMasterEmitter(this);
        yield return new PluginListReplacedEmitter(this);
        yield return new GameInisTakenOverEmitter(this);
        if (TestFileIniFiles.Count > 0)
            yield return new TestFileEntriesEmitter(this);
        foreach (var emitter in base.CreateEmitters(provider))
            yield return emitter;
    }

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var files = base.IntrinsicFiles(loadout, provider);
        // The mechanism can depend on the install, so a loadout that is not valid gets no plugin list.
        if (!loadout.IsValid()) return files;

        foreach (var file in PluginList(loadout.InstallationInstance).IntrinsicFiles(this, loadout, provider))
            files[file.Path] = file;
        foreach (var file in GameInis.IntrinsicFiles(this, loadout, provider))
            files[file.Path] = file;
        return files;
    }

    /// <summary>
    /// The INI files a loadout keeps its own copy of, in the order a Mod Organizer profile lists them (see
    /// <see cref="CreationEngineMo2Target.OwnedIniFiles"/>). Only the games with any declare
    /// <see cref="IHasIniFiles"/>, which is what shows the "Own the game INIs" choice.
    /// </summary>
    public IniSet IniFiles => new(CreationEngineMo2Target.OwnedIniFiles(TSelf.GameId).Select(p => p.FileName.ToString()).ToArray());

    GamePath IGameIniFiles.IniPath(string fileName) =>
        CreationEngineMo2Target.OwnedIniFiles(TSelf.GameId).Single(p => p.FileName.ToString() == fileName);

    /// <summary>
    /// The app's keys go in the first file that is not a Prefs or editor file, which the game reads before the
    /// Custom one the user keeps their own overrides in.
    /// </summary>
    GamePath? IGameIniFiles.ManagedKeysFile
    {
        get
        {
            foreach (var path in CreationEngineMo2Target.OwnedIniFiles(TSelf.GameId))
            {
                var name = path.FileName.ToString();
                if (!name.EndsWith("Prefs.ini", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("GECK", StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            return null;
        }
    }

    /// <summary>
    /// Loose files only override archived ones when the game stops comparing their dates, which every Bethesda
    /// title of this engine needs for a modded load order.
    /// </summary>
    IReadOnlyList<IniKeyEdit> IGameIniFiles.ManagedKeys(Loadout.ReadOnly loadout) =>
        [new IniKeyEdit("Archive", "bInvalidateOlderFiles", "1"), .. SaveRedirectIniKeys.For(this, loadout)];
}
