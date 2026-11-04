using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

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

        if (!await fileStore.HaveFile(file.Identity)) return null;

        Sha256Value digest;
        await using (var stream = await fileStore.GetFileStream(file.Identity, cancellationToken))
        {
            digest = await Sha256Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        }

        using var tx = connection.BeginTransaction();
        tx.Add(file.Id, LibraryFile.Sha256, digest);
        await tx.Commit();
        return digest;
    }

    /// <summary>
    /// Whether the file store holds the file's bytes.
    /// </summary>
    public ValueTask<bool> HasBytes(LibraryFile.ReadOnly file) => fileStore.HaveFile(file.Identity);

    /// <summary>
    /// True for a row that has no SHA-256 and whose bytes are not in the file store, such as the record of a download
    /// that never finished. Nothing can pin or verify it, and unlike <see cref="EnsureSha256"/> this never writes.
    /// </summary>
    public async ValueTask<bool> IsDangling(LibraryFile.ReadOnly file)
    {
        if (file.Sha256.HasValue) return false;

        var current = LibraryFile.Load(connection.Db, file.Id);
        if (current.IsValid() && current.Sha256.HasValue) return false;

        return !await HasBytes(file);
    }

    /// <summary>
    /// Returns the recorded xxHash64, or hashes the bytes from the file store, writes the value back and returns
    /// it. Returns null when the store holds no bytes for the file, and always for an archive: the store never
    /// holds an archive's own bytes, so a row from before the value was recorded cannot be back-filled.
    /// </summary>
    public async ValueTask<FileIdentity?> EnsureXxHash64(LibraryFile.ReadOnly file, CancellationToken cancellationToken = default)
    {
        var computed = await ComputeXxHash64(file, cancellationToken);
        if (computed is not { } found) return null;
        if (found.NeedsRecording) await RecordXxHash64([(file.Id, found.XxHash64)]);
        return found.XxHash64;
    }

    /// <summary>
    /// The xxHash64 of the file, from the row when it has one and otherwise from the bytes in the file store, without
    /// writing it back. <see cref="XxHash64Result.NeedsRecording"/> says the row lacks the value, so a caller that hashes
    /// many files can write them together with <see cref="RecordXxHash64"/> instead of committing once per file.
    /// </summary>
    public async ValueTask<XxHash64Result?> ComputeXxHash64(LibraryFile.ReadOnly file, CancellationToken cancellationToken = default)
    {
        if (LibraryFile.XxHash64.TryGetValue(file, out var recorded)) return new XxHash64Result(FileIdentity.XxHash64(recorded), NeedsRecording: false);

        var current = LibraryFile.Load(connection.Db, file.Id);
        if (current.IsValid() && LibraryFile.XxHash64.TryGetValue(current, out var written)) return new XxHash64Result(FileIdentity.XxHash64(written), NeedsRecording: false);

        if (file.TryGetAsLibraryArchive(out _)) return null;
        if (!await fileStore.HaveFile(file.Identity)) return null;

        Hash digest;
        await using (var stream = await fileStore.GetFileStream(file.Identity, cancellationToken))
        {
            digest = await Xx64Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        }
        return new XxHash64Result(FileIdentity.XxHash64(digest), NeedsRecording: true);
    }

    /// <summary>Writes the xxHash64 of each file in one commit.</summary>
    public async Task RecordXxHash64(IReadOnlyCollection<(EntityId File, FileIdentity XxHash64)> hashes)
    {
        if (hashes.Count == 0) return;
        using var tx = connection.BeginTransaction();
        foreach (var (file, identity) in hashes)
        {
            if (!identity.TryXxHash64(out var digest)) throw new ArgumentException($"{identity} is not an xxHash64", nameof(hashes));
            tx.Add(file, LibraryFile.XxHash64, digest);
        }
        await tx.Commit();
    }
}

/// <summary>An xxHash64 of a library file and whether the file's row still has to be given it.</summary>
public readonly record struct XxHash64Result(FileIdentity XxHash64, bool NeedsRecording);
