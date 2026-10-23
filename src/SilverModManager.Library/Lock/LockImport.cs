using JetBrains.Annotations;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.Sources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Library.Lock;

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
public sealed record LockPlanItem(LockEntry Entry, LockAction Action, string Message, LibraryFile.ReadOnly? Existing = null);

[PublicAPI]
public sealed record LockImportPlan(IReadOnlyList<LockPlanItem> Items, NexusModsGameId? Game, IGameData? GameData = null);

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
public sealed class LockImport(IConnection connection, IEnumerable<ILibrarySource> sources, ILibraryService libraryService)
{
    /// <summary>
    /// Decides what to do with each entry without changing anything. An entry whose digest is empty or malformed can
    /// never be verified, so it fails here even when a file with that name is already in the library.
    /// </summary>
    /// <param name="lockFile">The lock to import.</param>
    /// <param name="game">The Nexus Mods game the file ids belong to. The lock carries only file ids.</param>
    /// <param name="cancellationToken"></param>
    /// <param name="gameData">The game of the loadout being imported into, for sources that need more than the Nexus Mods id.</param>
    public ValueTask<LockImportPlan> Plan(LockFile lockFile, NexusModsGameId? game, CancellationToken cancellationToken = default, IGameData? gameData = null)
    {
        var context = new LibrarySourceContext(connection, gameData, game);
        var items = lockFile.Entries.Select(entry => PlanEntry(entry, context)).ToArray();
        return ValueTask.FromResult(new LockImportPlan(items, game, gameData));
    }

    private LockPlanItem PlanEntry(LockEntry entry, LibrarySourceContext context)
    {
        if (!Sha256Value.TryParse(entry.Sha256, out var expected))
            return new LockPlanItem(entry, LockAction.Fail, $"{entry.Id}: the lock has no valid sha256 for {entry.Asset}, so a download could not be verified.");

        var present = LibraryFile.FindBySha256(connection.Db, expected).FirstOrDefault(file => file.IsValid());
        if (present.IsValid())
            return new LockPlanItem(entry, LockAction.Skip, $"{entry.Id}: {entry.Asset} is already in the library with the locked sha256.", present);

        // Only a download needs the source, so a file already in the library verifies without it.
        if (FindSource(entry.Source) is not { } source)
            return new LockPlanItem(entry, LockAction.Fail, UnknownSource(entry));

        if (source.WhyNotDownloadable(entry.ToSourceEntry(), context) is { } reason)
            return new LockPlanItem(entry, LockAction.Fail, reason);

        return new LockPlanItem(entry, LockAction.Download, $"{entry.Id}: download {entry.Asset} ({entry.Version}) from {source.Name}.");
    }

    private ILibrarySource? FindSource(string name) => sources.FirstOrDefault(source => source.Name == name);

    private string UnknownSource(LockEntry entry) => $"{entry.Id}: unknown source '{entry.Source}'; the registered sources are {string.Join(", ", sources.Select(source => source.Name))}.";

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
                    var outcome = await Download(item, new LibrarySourceContext(connection, plan.GameData, plan.Game), cancellationToken);
                    (outcome.File is null ? failed : verified).Add(outcome);
                    break;
            }
        }

        return new LockImportSummary(verified, failed, skipped);
    }

    private async ValueTask<LockOutcome> Download(LockPlanItem item, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        var entry = item.Entry;
        if (FindSource(entry.Source) is not { } source)
            return new LockOutcome(entry, UnknownSource(entry));

        LibraryFile.ReadOnly file;
        try
        {
            file = await source.Download(entry.ToSourceEntry(), Sha256Value.FromHex(entry.Sha256), context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LockOutcome(entry, $"{entry.Id}: {exception.Message}");
        }

        var fresh = LibraryFile.Load(connection.Db, file.Id);
        Sha256Value? actual = fresh.IsValid() && fresh.Sha256.HasValue ? fresh.Sha256.Value : null;
        if (Sha256Match.Matches(actual, entry.Sha256))
            return new LockOutcome(entry, $"{entry.Id}: {entry.Asset} verified.", fresh);

        await libraryService.RemoveLibraryItems([file.AsLibraryItem()]);
        var got = actual?.ToString() ?? "no digest";
        return new LockOutcome(entry, $"{entry.Id}: {entry.Asset} has sha256 {got}, but the lock pins {entry.Sha256}; the download was removed from the library.");
    }
}
