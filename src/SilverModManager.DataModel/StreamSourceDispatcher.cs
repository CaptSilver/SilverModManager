using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel;

public class StreamSourceDispatcher : IStreamSourceDispatcher
{
    private readonly IReadOnlyStreamSource[] _sources;

    public StreamSourceDispatcher(IEnumerable<IReadOnlyStreamSource> sources)
    {
        _sources = sources.OrderBy(x => x.Priority).ToArray();
    }

    public async ValueTask<Stream?> OpenAsync(Hash hash, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            var result = await source.OpenAsync(hash, cancellationToken);
            if (result != null) return result;
        }
        return null;
    }

    public async ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity.TryXxHash3(out var hash)) return await OpenAsync(hash, cancellationToken);

        foreach (var source in _sources.OfType<IIdentityStreamSource>())
        {
            var result = await source.OpenAsync(identity, cancellationToken);
            if (result != null) return result;
        }
        return null;
    }

    public bool Exists(Hash hash)
    {
        foreach (var source in _sources)
            if (source.Exists(hash)) return true;
        return false;
    }
}
