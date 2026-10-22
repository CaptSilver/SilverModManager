using NexusMods.Abstractions.Games;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;


using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Abstractions;

/// <summary>
/// A Bethesda game whose plugin headers the load order reader can parse.
/// </summary>
public interface ICreationEngineGame : IBethesdaGame
{
    public GamePath PluginsFile { get; }

    /// <summary>How <see cref="PluginsFile"/> marks an active plugin.</summary>
    public PluginListFormat PluginListFormat { get; }
    /// <summary>
    /// Parse a plugin file. Reads only the header record of the file: its masters, flags and version
    /// </summary>
    /// <returns>
    /// The plugin header, or <c>null</c> when no stream source can supply the bytes for <paramref name="hash"/>.
    /// That happens for a game file whose recorded build is no longer on disk.
    /// </returns>
    public ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null);
}
