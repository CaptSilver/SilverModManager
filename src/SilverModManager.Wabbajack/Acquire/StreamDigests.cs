using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using DigestHasher = SilverModManager.Sdk.Hashes.MultiHasher<NexusMods.Hashing.xxHash3.Hash, System.IO.Hashing.XxHash3, SilverModManager.Sdk.Hashes.Xx3Hasher, NexusMods.Hashing.xxHash3.Hash, System.IO.Hashing.XxHash64, SilverModManager.Sdk.Hashes.Xx64Hasher>;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Both digests the library and a modlist use, computed over one read of a stream.</summary>
internal static class StreamDigests
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// xxHash3 (the file store's key), xxHash64 (the modlist's) and the length in one pass. Counts bytes read because
    /// an opener's stream may not report a length or position.
    /// </summary>
    public static async Task<(Hash XxHash3, Hash XxHash64, Size Size)> HashAndCount(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        var state = DigestHasher.Initialize();
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            state = DigestHasher.Update(state, buffer, 0, read);
            length += read;
        }
        var hashes = DigestHasher.Finish(state);
        return (hashes.Hash1, hashes.Hash2, Size.FromLong(length));
    }
}
