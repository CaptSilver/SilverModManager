using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Library.Lock;

/// <summary>
/// Fetches the files a lock names and adds them to the library.
/// </summary>
[PublicAPI]
public interface ILockDownloader
{
    /// <summary>
    /// Downloads one Nexus Mods file by its id and adds it to the library.
    /// </summary>
    ValueTask<LibraryFile.ReadOnly> DownloadNexusFile(FileId fileId, NexusModsGameId game, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the asset of a GitHub release and adds it to the library. The download must not be added when its
    /// bytes do not match <paramref name="expected"/>.
    /// </summary>
    ValueTask<LibraryFile.ReadOnly> DownloadGitHubAsset(GitHubRepositoryRef repository, string tag, string asset, Sha256Value expected, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a library file again.
    /// </summary>
    ValueTask Discard(LibraryFile.ReadOnly file);
}

[PublicAPI]
public enum LockAction
{
    /// <summary>The library already holds the file with the locked digest.</summary>
    Skip,
    Download,
    Fail,
}

/// <summary>
/// What import will do with one lock entry, and why.
/// </summary>
[PublicAPI]
public sealed record LockPlanItem(LockEntry Entry, LockAction Action, string Message, LibraryFile.ReadOnly? Existing = null, GitHubRepositoryRef? Repository = null, FileId? NexusFileId = null);

[PublicAPI]
public sealed record LockImportPlan(IReadOnlyList<LockPlanItem> Items, NexusModsGameId? Game);

/// <summary>
/// The outcome of one entry. <see cref="File"/> is the library file that holds the verified bytes, when there is one.
/// </summary>
[PublicAPI]
public sealed record LockOutcome(LockEntry Entry, string Message, LibraryFile.ReadOnly? File = null);

[PublicAPI]
public sealed record LockImportSummary(IReadOnlyList<LockOutcome> Verified, IReadOnlyList<LockOutcome> Failed, IReadOnlyList<LockOutcome> Skipped);

/// <summary>
/// Brings the files a lock names into the library. Every download is checked against the digest in the lock, and a
/// file that does not match is removed again; one entry failing never stops the others.
/// </summary>
[PublicAPI]
public sealed class LockImport(IConnection connection, ILockDownloader downloader)
{
    /// <summary>
    /// Decides what to do with each entry without changing anything. An entry whose digest is empty or malformed can
    /// never be verified, so it fails here even when a file with that name is already in the library.
    /// </summary>
    /// <param name="lockFile">The lock to import.</param>
    /// <param name="game">The Nexus Mods game the file ids belong to. The lock carries only file ids.</param>
    /// <param name="cancellationToken"></param>
    public ValueTask<LockImportPlan> Plan(LockFile lockFile, NexusModsGameId? game, CancellationToken cancellationToken = default)
    {
        var items = lockFile.Entries.Select(entry => PlanEntry(entry, game)).ToArray();
        return ValueTask.FromResult(new LockImportPlan(items, game));
    }

    private LockPlanItem PlanEntry(LockEntry entry, NexusModsGameId? game)
    {
        if (!Sha256Value.TryParse(entry.Sha256, out var expected))
            return new LockPlanItem(entry, LockAction.Fail, $"{entry.Id}: the lock has no valid sha256 for {entry.Asset}, so a download could not be verified.");

        var present = LibraryFile.FindBySha256(connection.Db, expected).FirstOrDefault(file => file.IsValid());
        if (present.IsValid())
            return new LockPlanItem(entry, LockAction.Skip, $"{entry.Id}: {entry.Asset} is already in the library with the locked sha256.", present);

        if (entry.Source == LockSources.Nexus)
        {
            if (game is null)
                return new LockPlanItem(entry, LockAction.Fail, $"{entry.Id}: the loadout's game has no Nexus Mods id, so file {entry.FileId} cannot be looked up.");
            if (!uint.TryParse(entry.FileId, out var fileNumber))
                return new LockPlanItem(entry, LockAction.Fail, $"{entry.Id}: the lock has no usable Nexus Mods file id ('{entry.FileId}').");
            return new LockPlanItem(entry, LockAction.Download, $"{entry.Id}: download Nexus Mods file {entry.FileId}.", NexusFileId: FileId.From(fileNumber));
        }

        if (FindRepository(entry.Asset) is not { } repository)
            return new LockPlanItem(entry, LockAction.Fail, $"{entry.Id}: no repository is known for {entry.Asset}; add one release of it to the library once so its repository is recorded.");
        return new LockPlanItem(entry, LockAction.Download, $"{entry.Id}: download {entry.Asset} of release {entry.Version} from {repository}.", Repository: repository);
    }

    // The lock names no repository, so a library file that was downloaded from a release under the same asset name is
    // the only place the importer can learn it from.
    private GitHubRepositoryRef? FindRepository(string asset)
    {
        foreach (var file in GitHubReleaseLibraryFile.All(connection.Db))
        {
            if (file.AssetName != asset) continue;
            if (GitHubReleaseLibraryFile.RepositoryId.TryGetValue(file, out var id)) return GitHubRepositoryRef.FromId(id);
            if (GitHubReleaseLibraryFile.Organization.TryGetValue(file, out var organization) && GitHubReleaseLibraryFile.Repository.TryGetValue(file, out var repository))
                return GitHubRepositoryRef.FromName(organization, repository);
        }

        return null;
    }

    /// <summary>
    /// Carries out a plan. A download is verified against the lock before it counts: bytes that do not match are
    /// removed from the library again and the entry fails.
    /// </summary>
    public async ValueTask<LockImportSummary> Apply(LockImportPlan plan, CancellationToken cancellationToken = default)
    {
        List<LockOutcome> verified = [], failed = [], skipped = [];

        foreach (var item in plan.Items)
        {
            switch (item.Action)
            {
                case LockAction.Skip:
                    skipped.Add(new LockOutcome(item.Entry, item.Message, item.Existing));
                    break;
                case LockAction.Fail:
                    failed.Add(new LockOutcome(item.Entry, item.Message));
                    break;
                default:
                    var outcome = await Download(item, plan.Game, cancellationToken);
                    (outcome.File is null ? failed : verified).Add(outcome);
                    break;
            }
        }

        return new LockImportSummary(verified, failed, skipped);
    }

    private async ValueTask<LockOutcome> Download(LockPlanItem item, NexusModsGameId? game, CancellationToken cancellationToken)
    {
        var entry = item.Entry;
        LibraryFile.ReadOnly file;
        try
        {
            file = item switch
            {
                { NexusFileId: { } fileId } when game is { } nexusGame => await downloader.DownloadNexusFile(fileId, nexusGame, cancellationToken),
                { Repository: { } repository } => await downloader.DownloadGitHubAsset(repository, entry.Version, entry.Asset, Sha256Value.FromHex(entry.Sha256), cancellationToken),
                _ => throw new InvalidOperationException($"{entry.Id}: the plan says to download {entry.Asset} but names no Nexus Mods file and game or repository to download it from."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LockOutcome(entry, $"{entry.Id}: {exception.Message}");
        }

        var fresh = LibraryFile.Load(connection.Db, file.Id);
        Sha256Value? actual = fresh.IsValid() && fresh.Sha256.HasValue ? fresh.Sha256.Value : null;
        if (Sha256Match.Matches(actual, entry.Sha256))
            return new LockOutcome(entry, $"{entry.Id}: {entry.Asset} verified.", fresh);

        await downloader.Discard(file);
        var got = actual?.ToString() ?? "no digest";
        return new LockOutcome(entry, $"{entry.Id}: {entry.Asset} has sha256 {got}, but the lock pins {entry.Sha256}; the download was removed from the library.");
    }
}
