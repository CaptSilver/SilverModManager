using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// What the backends that serve files from the loose staging store do before they hand one out.
/// </summary>
public static class StagedFiles
{
    /// <summary>
    /// Stages every identity and returns the staged paths. A file that was staged before this call is hashed
    /// first, and a file whose bytes no longer match its name is released and staged again.
    /// </summary>
    /// <remarks>
    /// A staged file is named by its bytes, but a write through a link to it changes the bytes and not the name, so a
    /// name is trusted only for a file this call just extracted.
    /// </remarks>
    public static async Task<Dictionary<FileIdentity, AbsolutePath>> EnsureVerified(DeploymentContext context, ILooseStagingStore store, IEnumerable<FileIdentity> identities, ILogger logger)
    {
        var installation = context.MetadataId;
        var storeFiles = new Dictionary<FileIdentity, AbsolutePath>();
        var reused = new List<FileIdentity>();
        foreach (var identity in identities)
        {
            if (store.Contains(installation, identity)) reused.Add(identity);
            storeFiles[identity] = await store.Ensure(installation, identity);
        }

        // A file Ensure just extracted is trusted. Only one that was already there can have been written through.
        await Parallel.ForEachAsync(reused, new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders }, async (identity, token) =>
        {
            var actual = FileIdentity.XxHash3(await FileHashing.XxHash3(storeFiles[identity], token));
            if (actual.Equals(identity)) return;

            logger.LogWarning(
                "Staged file {StoreFile} holds {Actual} instead of {Hash}, something wrote through a link to it. Staging it again",
                storeFiles[identity], actual, identity
            );
            store.Release(installation, identity);
            await store.Ensure(installation, identity, token);
        });

        return storeFiles;
    }
}
