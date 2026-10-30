using JetBrains.Annotations;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Resources;

/// <summary>
/// Resource loader that returns a stream of a file in the file store.
/// </summary>
[PublicAPI]
public sealed class FileStoreStreamLoader : IResourceLoader<FileIdentity, Stream>
{
    private readonly IFileStore _fileStore;
    
    /// <summary>
    /// Constructor.
    /// </summary>
    public FileStoreStreamLoader(IFileStore fileStore)
    {
        _fileStore = fileStore;
    }
    
    /// <inheritdoc/>
    public async ValueTask<Resource<Stream>> LoadResourceAsync(FileIdentity resourceIdentifier, CancellationToken cancellationToken)
    {
        var stream = await _fileStore.GetFileStream(resourceIdentifier, cancellationToken);
        return new Resource<Stream>
        {
            Data = stream,
        };
    }
}
