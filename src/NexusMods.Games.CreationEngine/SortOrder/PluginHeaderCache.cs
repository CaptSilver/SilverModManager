using System.Collections.Concurrent;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// Plugin headers by content hash and extension. A header only changes when the bytes do, so each is parsed once and
/// every later lookup is a dictionary read, which keeps sorting off the file store.
/// </summary>
/// <remarks>
/// A plugin that was tried and could not be read is cached as <c>null</c> too, so a broken plugin is not
/// re-read on every refresh. The cached <see cref="PluginHeader.FileName"/> is a placeholder: the same bytes
/// can sit under different names. The flag checks that look at the extension (<see cref="PluginFlags.IsMaster"/>
/// for games that master by extension, <see cref="PluginFlags.IsLight"/>) would read the placeholder, so take
/// the header as <c>header with { FileName = nameFromLoadout }</c> before asking them.
/// </remarks>
public sealed class PluginHeaderCache(ICreationEngineGame game)
{
    // The extension is part of the key because a game can read the same bytes differently by extension
    // (Morrowind takes a .omwscripts file as a script list, not a TES3 record). The rest of the name does not
    // change the header, so it is left out.
    private readonly ConcurrentDictionary<(Hash Hash, Extension Extension), Lazy<Task<PluginHeader?>>> _headers = new();

    /// <summary>
    /// Parses every plugin that has not been tried yet. An unreadable plugin is recorded as <c>null</c> and does not throw.
    /// Concurrent calls for the same plugin share one parse.
    /// </summary>
    public async ValueTask WarmAsync(IEnumerable<(Hash Hash, string Name)> plugins)
    {
        var pending = new List<Task>();
        foreach (var (hash, name) in plugins)
        {
            var path = RelativePath.FromUnsanitizedInput(name);
            var key = (hash, path.Extension);
            var lazy = _headers.GetOrAdd(key, _ => new Lazy<Task<PluginHeader?>>(() => Parse(hash, path)));
            pending.Add(AwaitOrForget(key, lazy));
        }

        await Task.WhenAll(pending);
    }

    // A parse that failed for a reason other than bad bytes (the store could not be read, say) is not an answer,
    // so it is dropped and the next warm tries again. Only this entry is removed, not one a later warm put back.
    private async Task AwaitOrForget((Hash Hash, Extension Extension) key, Lazy<Task<PluginHeader?>> lazy)
    {
        try
        {
            await lazy.Value;
        }
        catch
        {
            _headers.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// True when the plugin was warmed. <paramref name="header"/> is <c>null</c> when it was warmed
    /// but no header could be read from it.
    /// </summary>
    public bool TryGet(Hash hash, string name, out PluginHeader? header)
    {
        header = null;
        if (!_headers.TryGetValue((hash, RelativePath.FromUnsanitizedInput(name).Extension), out var lazy) || !lazy.Value.IsCompletedSuccessfully)
            return false;

        header = lazy.Value.Result;
        return true;
    }

    private async Task<PluginHeader?> Parse(Hash hash, RelativePath name)
    {
        try
        {
            return await game.ParsePlugin(hash, name);
        }
        catch (InvalidPluginException)
        {
            return null;
        }
    }
}
