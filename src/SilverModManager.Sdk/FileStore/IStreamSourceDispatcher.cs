using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.FileStore;

public interface IStreamSourceDispatcher
{
    public ValueTask<Stream?> OpenAsync(Hash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the bytes behind an identity of any kind. An xxHash3 identity asks every source as
    /// <see cref="OpenAsync(Hash, CancellationToken)"/> does; another kind only reaches the sources that know files by it.
    /// </summary>
    public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default);

    public bool Exists(Hash hash);
}
