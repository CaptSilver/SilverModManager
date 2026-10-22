using System.Collections.Concurrent;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Hashing.xxHash3;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// Plugin headers by content hash. A header only changes when the bytes do, so a hash is parsed once and
/// every later lookup is a dictionary read, which keeps sorting off the file store.
/// </summary>
/// <remarks>
/// A hash that was tried and could not be read is cached as <c>null</c> too, so a broken plugin is not
/// re-read on every refresh. The cached <see cref="PluginHeader.FileName"/> is a placeholder: the same bytes
/// can sit under different names. The flag checks that look at the extension (<see cref="PluginFlags.IsMaster"/>
/// for games that master by extension, <see cref="PluginFlags.IsLight"/>) would read the placeholder, so take
/// the header as <c>header with { FileName = nameFromLoadout }</c> before asking them.
/// </remarks>
public sealed class PluginHeaderCache(ICreationEngineGame game)
{
    private readonly ConcurrentDictionary<Hash, PluginHeader?> _headers = new();

    /// <summary>
    /// Parses every hash that has not been tried yet. An unreadable plugin is recorded as <c>null</c> and does not throw.
    /// </summary>
    public async ValueTask WarmAsync(IEnumerable<Hash> hashes)
    {
        foreach (var hash in hashes.Distinct())
        {
            if (_headers.ContainsKey(hash)) continue;
            _headers[hash] = await Parse(hash);
        }
    }

    /// <summary>
    /// True when <paramref name="hash"/> was warmed. <paramref name="header"/> is <c>null</c> when it was warmed
    /// but no header could be read from it.
    /// </summary>
    public bool TryGet(Hash hash, out PluginHeader? header) => _headers.TryGetValue(hash, out header);

    private async ValueTask<PluginHeader?> Parse(Hash hash)
    {
        try
        {
            return await game.ParsePlugin(hash);
        }
        catch (InvalidPluginException)
        {
            return null;
        }
    }
}
