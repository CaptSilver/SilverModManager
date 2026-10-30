using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.FileStore;

public interface IStreamSourceDispatcher
{
    /// <summary>
    /// Opens the bytes behind an identity of any kind. An xxHash3 identity asks every source; another kind only
    /// reaches the sources that know files by it (<see cref="IIdentityStreamSource"/>).
    /// </summary>
    public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default);

    public bool Exists(FileIdentity identity);
}
