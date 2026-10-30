using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.FileStore;

/// <summary>
/// Provides a read-only stream source. Streams can be opened by identity.
/// </summary>
public interface IReadOnlyStreamSource
{
    /// <summary>
    /// Lookup a stream by identity. The result will be seekable. A source that does not know files by
    /// the identity's kind answers null.
    /// </summary>
    public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return true if the stream exists in the source
    /// </summary>
    public bool Exists(FileIdentity identity);

    /// <summary>
    /// Get the priority of this source.
    /// </summary>
    public SourcePriority Priority { get; }
}
