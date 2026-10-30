using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Threading;

namespace SilverModManager.Benchmarks.Benchmarks.Loadouts.Harness;

public class DummyFileStore : IFileStore
{
    public ValueTask<bool> HaveFile(FileIdentity identity)
    {
        return ValueTask.FromResult(false);
    }

    public Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
    {
        progress?.Report(1.0);
        return Task.CompletedTask;
    }

    public Task ExtractFiles(IEnumerable<(FileIdentity Identity, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null)
    {
        return Task.CompletedTask;
    }

    public Task<Dictionary<FileIdentity, byte[]>> ExtractFiles(IEnumerable<FileIdentity> files, CancellationToken token = default)
    {
        return Task.FromResult(new Dictionary<FileIdentity, byte[]>());
    }

    public Task<Stream> GetFileStream(FileIdentity identity, CancellationToken token = default)
    {
        return null!;
    }

    public Task<byte[]> Load(FileIdentity identity, CancellationToken token = default)
    {
        return Task.FromResult(Array.Empty<byte>());
    }

    public HashSet<ulong> GetFileHashes()
    {
        return [];
    }

    public AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => throw new NotImplementedException(); // Only used by GC, so never called right now.
    
    public void ReloadCaches()
    {
        
    }
}
