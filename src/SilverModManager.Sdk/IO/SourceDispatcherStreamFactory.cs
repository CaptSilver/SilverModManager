using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.FileStore;

namespace SilverModManager.Sdk.IO;

public class SourceDispatcherStreamFactory : IStreamFactory
{
    private readonly IStreamSourceDispatcher _dispatcher;
    private readonly FileIdentity _identity;
    
    public SourceDispatcherStreamFactory(RelativePath fileName, FileIdentity identity, IStreamSourceDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _identity = identity;
        FileName = fileName;
    }

    public RelativePath FileName { get; }
    public async ValueTask<Stream> GetStreamAsync()
    {
        var stream = await _dispatcher.OpenAsync(_identity);
        return stream ?? throw new FileNotFoundException($"The file {FileName} with hash '{_identity}' could not be found in any stream source.");
    }
}
