using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Emitters;
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

    /// <summary>The header layout this game's plugins use.</summary>
    protected abstract PluginFormat HeaderFormat { get; }

    public abstract GamePath PluginsFile { get; }
    public virtual PluginListFormat PluginListFormat => PluginListFormat.Star;

    public async ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null)
    {
        await using var stream = await _streamSource.OpenAsync(hash);
        if (stream is null) return null;

        return PluginHeaderReader.Read(stream, HeaderFormat, name?.FileName.ToString() ?? "unknown.esm");
    }

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
        [new MissingMasterEmitter(this), .. base.CreateEmitters(provider)];

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var files = base.IntrinsicFiles(loadout, provider);
        var pluginsFile = new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), this, provider.GetRequiredService<ISorter>());
        files[pluginsFile.Path] = pluginsFile;
        return files;
    }
}
