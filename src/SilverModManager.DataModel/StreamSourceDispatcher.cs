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

    public async ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default)
    {
        foreach (var source in SourcesFor(identity))
        {
            var result = await source.OpenAsync(identity, cancellationToken);
            if (result != null) return result;
        }
        return null;
    }

    public bool Exists(FileIdentity identity)
    {
        foreach (var source in SourcesFor(identity))
            if (source.Exists(identity)) return true;
        return false;
    }

    // Every source is keyed by xxHash3; only the ones that also know files by another name can answer anything else.
    private IEnumerable<IReadOnlyStreamSource> SourcesFor(FileIdentity identity) =>
        identity.Kind == HashKind.XxHash3 ? _sources : _sources.OfType<IIdentityStreamSource>();
}
