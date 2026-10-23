using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;

namespace NexusMods.Library;

/// <summary>
/// Supplies the SHA-256 and xxHash64 of library files. Files added before the digest was recorded get it on first need
/// from the file store, so no scanning migration is required.
/// </summary>
public sealed class LibraryFileDigests(IFileStore fileStore, IConnection connection)
{
    /// <summary>
    /// Returns the stored digest, or streams the bytes from the file store, writes the digest back and
    /// returns it. Returns null when the store holds no bytes for the file.
    /// </summary>
    public async ValueTask<Sha256Value?> EnsureSha256(LibraryFile.ReadOnly file, CancellationToken cancellationToken = default)
    {
        if (file.Sha256.HasValue) return file.Sha256.Value;

        // The caller's snapshot may predate an earlier write-back; the current row saves a full re-read.
        var current = LibraryFile.Load(connection.Db, file.Id);
        if (current.IsValid() && current.Sha256.HasValue) return current.Sha256.Value;

        if (!await fileStore.HaveFile(file.Hash)) return null;

        Sha256Value digest;
        await using (var stream = await fileStore.GetFileStream(file.Hash, cancellationToken))
        {
            digest = await Sha256Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        }

        using var tx = connection.BeginTransaction();
        tx.Add(file.Id, LibraryFile.Sha256, digest);
        await tx.Commit();
        return digest;
    }

    /// <summary>
    /// Returns the recorded xxHash64, or hashes the bytes from the file store, writes the value back and returns
    /// it. Returns null when the store holds no bytes for the file, and always for an archive: the store never
    /// holds an archive's own bytes, so a row from before the value was recorded cannot be back-filled.
    /// </summary>
    public async ValueTask<Hash?> EnsureXxHash64(LibraryFile.ReadOnly file, CancellationToken cancellationToken = default)
    {
        if (file.XxHash64.HasValue) return file.XxHash64.Value;

        var current = LibraryFile.Load(connection.Db, file.Id);
        if (current.IsValid() && current.XxHash64.HasValue) return current.XxHash64.Value;

        if (file.TryGetAsLibraryArchive(out _)) return null;
        if (!await fileStore.HaveFile(file.Hash)) return null;

        Hash digest;
        await using (var stream = await fileStore.GetFileStream(file.Hash, cancellationToken))
        {
            digest = await Xx64Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        }

        using var tx = connection.BeginTransaction();
        tx.Add(file.Id, LibraryFile.XxHash64, digest);
        await tx.Commit();
        return digest;
    }
}
