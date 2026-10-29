using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes;

public static class FileIdentityResolverExtensions
{
    /// <summary>
    /// The store key the bytes are archived under, as an xxHash3 identity: the first candidate the store holds.
    /// Every size-matching row is tried, not just the first, because two rows can share a SHA-1 and differ in
    /// xxHash3 and only one of them may be archived. Null when the store holds none.
    /// </summary>
    public static async ValueTask<FileIdentity?> StoreKeyAsync(this IFileIdentityResolver resolver, IFileStore store, FileIdentity identity, Size size)
    {
        foreach (var candidate in resolver.StoreKeyCandidates(identity, size))
        {
            if (await store.HaveFile(candidate.XxHash3())) return candidate;
        }
        return null;
    }
}
