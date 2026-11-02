using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Wabbajack.Tests;

/// <summary>A file store that hands every call to another one; a test overrides the call it wants to watch or break.</summary>
internal abstract class ForwardingFileStore(IFileStore inner) : IFileStore
{
    public virtual ValueTask<bool> HaveFile(FileIdentity identity) => inner.HaveFile(identity);

    public virtual Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        => inner.BackupFiles(backups, deduplicate, token, progress);

    public Task ExtractFiles(IEnumerable<(FileIdentity Identity, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null)
        => inner.ExtractFiles(files, token, progress);

    public Task<Dictionary<FileIdentity, byte[]>> ExtractFiles(IEnumerable<FileIdentity> files, CancellationToken token = default) => inner.ExtractFiles(files, token);

    public Task<Stream> GetFileStream(FileIdentity identity, CancellationToken token = default) => inner.GetFileStream(identity, token);

    public Task<byte[]> Load(FileIdentity identity, CancellationToken token = default) => inner.Load(identity, token);

    public SilverModManager.Sdk.Threading.AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => inner.WriteLock();

    public void ReloadCaches() => inner.ReloadCaches();

    public event Action? ArchivesChanged
    {
        add => inner.ArchivesChanged += value;
        remove => inner.ArchivesChanged -= value;
    }
}

/// <summary>Forwards to a store and counts the calls that back files up.</summary>
internal sealed class CountingBackups(IFileStore inner) : ForwardingFileStore(inner)
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public override Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
    {
        Interlocked.Increment(ref _calls);
        return base.BackupFiles(backups, deduplicate, token, progress);
    }
}
