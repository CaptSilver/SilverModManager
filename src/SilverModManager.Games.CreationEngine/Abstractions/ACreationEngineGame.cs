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
public abstract class ACreationEngineGame<TSelf> : ABethesdaGame<TSelf>, ICreationEngineGame, IMo2InstanceTarget, IGameIniFiles, IPerLoadoutSaves
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
        yield return new GameIniChangedByGameEmitter(this, provider);
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
    GamePath? IGameIniFiles.ManagedKeysFile => GameInis.MainIni(CreationEngineMo2Target.OwnedIniFiles(TSelf.GameId));

    /// <summary>
    /// Loose files only override archived ones when the game stops comparing their dates, which every Bethesda
    /// title of this engine needs for a modded load order.
    /// </summary>
    IReadOnlyList<IniKeyEdit> IGameIniFiles.ManagedKeys(Loadout.ReadOnly loadout) =>
        [.. ArchiveIniKeys, .. SaveRedirectKeys(loadout)];

    /// <summary>The archive setting the app owns in the main INI. Morrowind has no such switch.</summary>
    protected virtual IReadOnlyList<IniKeyEdit> ArchiveIniKeys => [new IniKeyEdit("Archive", "bInvalidateOlderFiles", "1")];

    Task IGameIniFiles.RenderDerivedKeys(IniDocument ini, GamePath path, Loadout.ReadOnly loadout) => RenderDerivedIniKeys(ini, path, loadout);

    bool IGameIniFiles.IsDerivedKey(GamePath path, string section, string key) => IsDerivedIniKey(path, section, key);

    /// <summary>Fills in what the INI at <paramref name="path"/> takes from the loadout rather than from its baseline. None by default.</summary>
    protected virtual Task RenderDerivedIniKeys(IniDocument ini, GamePath path, Loadout.ReadOnly loadout) => Task.CompletedTask;

    protected virtual bool IsDerivedIniKey(GamePath path, string section, string key) => false;

    /// <summary>
    /// The extensions of the files a save is made of, such as the save itself and the script extender's co-save.
    /// A <c>.bak</c> copy of any of them follows the save.
    /// </summary>
    protected abstract string[] SaveExtensions { get; }

    /// <summary>Why this title cannot keep saves per loadout, or null when it can.</summary>
    protected virtual string? SaveRedirectUnsupportedReason => null;

    /// <summary>
    /// True when the game only honours <c>sLocalSavePath</c> once <c>bUseMyGamesDirectory</c> sends it to My Games.
    /// </summary>
    protected virtual bool SaveRedirectNeedsMyGamesDirectory => false;

    /// <inheritdoc />
    /// <remarks>
    /// The save folder is only ever written into the loadout's own copy of the INI, and a loadout that does not own
    /// its INIs has none, so the redirect would silently do nothing.
    /// </remarks>
    public SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout) =>
        SaveRedirectUnsupportedReason is { } reason
            ? new SaveRedirectPlan.Unsupported(reason)
            : !Loadout.OwnsGameInis.Get(loadout)
                ? new SaveRedirectPlan.Unsupported("Needs 'Own the game INIs': the save folder is set through the game's INI.")
                : new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\");

    private IEnumerable<IniKeyEdit> SaveRedirectKeys(Loadout.ReadOnly loadout)
    {
        var keys = SaveRedirectIniKeys.For(this, loadout).ToList();
        if (keys.Count > 0 && SaveRedirectNeedsMyGamesDirectory) keys.Add(new IniKeyEdit("General", "bUseMyGamesDirectory", "1"));
        return keys;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="saveRoot"/> is the folder the INI's save path is relative to (My Games/&lt;title&gt;). The
    /// saves the game writes today sit loose in its <c>Saves</c> folder; folders under it, including other loadouts'
    /// slots, are left alone, as is any file the slot already has.
    /// </remarks>
    public SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot)
    {
        var saves = saveRoot / "Saves";
        if (SaveRedirectUnsupportedReason is not null || !saves.DirectoryExists()) return null;

        var slot = LoadoutSaveSlot.IdOf(loadout);
        var copies = saves.EnumerateFiles(recursive: false)
            .Where(IsSaveFile)
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SaveAdoptionPlan.Copy(file, saves / slot / file.FileName))
            .Where(copy => !copy.Destination.FileExists)
            .ToList();
        return copies.Count == 0
            ? null
            : new SaveAdoptionPlan($"Copy the saves in {saves} to {saves / slot}, so this loadout continues from the saves you play today.", copies);
    }

    private bool IsSaveFile(AbsolutePath file)
    {
        var name = file.FileName.ToString();
        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) name = name[..^".bak".Length];
        return SaveExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}
