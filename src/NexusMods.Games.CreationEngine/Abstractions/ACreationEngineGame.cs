using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Abstractions;

/// <summary>
/// A Bethesda title whose plugin headers the load order reader can parse: adds the Plugins.txt intrinsic file and the
/// missing-master check on top of <see cref="ABethesdaGame{TSelf}"/>.
/// </summary>
public abstract class ACreationEngineGame<TSelf> : ABethesdaGame<TSelf>, ICreationEngineGame
    where TSelf : ACreationEngineGame<TSelf>, IGameData<TSelf>
{
    private readonly IStreamSourceDispatcher _streamSource;

    protected ACreationEngineGame(IServiceProvider provider) : base(provider)
    {
        _streamSource = provider.GetRequiredService<IStreamSourceDispatcher>();
    }

    public abstract PluginFormat HeaderFormat { get; }

    public abstract GamePath PluginsFile { get; }
    public abstract IPluginListMechanism PluginList(GameInstallation installation);

    // libloadorder game_settings.rs: hardcoded_plugins.
    public abstract IReadOnlyList<string> HardcodedPlugins { get; }

    public virtual GamePath? CreationClubListFile => null;

    /// <summary>A list file the game reads only when <see cref="CreationClubListFile"/> is absent from disk.</summary>
    protected virtual GamePath? FallbackCreationClubListFile => null;

    public virtual IReadOnlyList<GamePath> TestFileIniFiles => [];

    public virtual Extension[] PluginExtensions => SupportsLightPlugins
        ? [KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL]
        : [KnownCEExtensions.ESM, KnownCEExtensions.ESP];

    public virtual bool SupportsLightPlugins => false;
    public virtual bool MasterByExtension => false;
    public virtual bool MasterFlagIgnored(GameInstallation installation) => false;

    /// <summary>Only Starfield lays its flag bits out differently, and needs the medium, update and blueprint kinds.</summary>
    protected virtual bool IsStarfield => false;

    public bool IsMaster(PluginHeader header, GameInstallation installation) =>
        PluginFlags.IsMaster(header, MasterByExtension, MasterFlagIgnored(installation));

    public bool IsLight(PluginHeader header) => PluginFlags.IsLight(header, SupportsLightPlugins, IsStarfield);
    public bool IsMedium(PluginHeader header) => PluginFlags.IsMedium(header, SupportsLightPlugins, IsStarfield);
    public bool IsUpdate(PluginHeader header) => PluginFlags.IsUpdate(header, IsStarfield);
    public bool IsBlueprint(PluginHeader header) => PluginFlags.IsBlueprint(header, IsStarfield);

    public IReadOnlyList<string> EarlyLoadingPlugins(GameInstallation installation)
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

    public async ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null)
    {
        await using var stream = await _streamSource.OpenAsync(hash);
        if (stream is null) return null;

        return PluginHeaderReader.Read(stream, HeaderFormat, name?.FileName.ToString() ?? "unknown.esm");
    }

    // One instance per game: it owns a header cache, so it is never a shared service.
    protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [new PluginSortOrderVariety(provider, this)];

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider)
    {
        yield return new MissingMasterEmitter(this);
        yield return new PluginListReplacedEmitter(this);
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
        return files;
    }
}
