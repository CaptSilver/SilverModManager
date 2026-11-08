using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Gives a list downloaded before the library recorded gallery identities its <c>MachineUrl</c> and
/// <c>Repository</c>. A file added from disk (no download address) is never matched, and neither is one that already
/// has an identity. A listing missing a repository only confirms exact hash matches: a name or title match might be
/// the survivor of an ambiguity the missing repository would have shown.
/// The gallery lists third-party repositories, so a repository other than the one the file came from must not be able
/// to claim it. An entry is only a candidate when its own download link is the one the file was fetched from, its
/// title is the list's own name, and no other repository offers a list with the same title or machine URL. The link
/// only proves which bytes were fetched, not who lists them, which is why the title is checked too. The cost: once the
/// real list moves to a new link, or is renamed, the file is no longer bound automatically; the user picks it with
/// <see cref="Assign"/>.
/// </summary>
public sealed class ModlistIdentityBackfill(IConnection connection, ILogger<ModlistIdentityBackfill> logger)
{
    // One at a time: a refresh and the start-up pass must not both write the same file.
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Writes the identity of every file with exactly one candidate. Never writes <c>GalleryDateUpdated</c>.</summary>
    public async Task Run(GalleryListing listing, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var db = connection.Db;
            using var tx = connection.BeginTransaction();
            var assigned = 0;
            foreach (var file in WabbajackModlistLibraryFile.All(db))
            {
                if (!file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl) || file.Contains(WabbajackModlistLibraryFile.MachineUrl)) continue;
                if (Candidates(file, listing) is not [var only]) continue;
                if (!string.Equals(only.Title, file.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (HasLookalikeElsewhere(only, listing)) continue;

                Write(tx, file, only);
                assigned++;
            }
            if (assigned == 0) return;

            await tx.Commit();
            logger.LogInformation("Recorded the gallery entry of {Count} modlist(s) downloaded earlier", assigned);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// The entries the file may be, from the most certain rule that finds any: the same bytes, the file name the
    /// downloader gives that entry, then the same title and author in the same game. Every rule only considers entries
    /// whose download link is the one the file was fetched from (<see cref="WabbajackModlistLibraryFile.GalleryDownloadUrl"/>),
    /// so a hash advertised by another repository's entry cannot claim the file. More than one is ambiguous and
    /// is left for the user to pick. The name rules are skipped on an incomplete listing.
    /// </summary>
    public IReadOnlyList<GalleryEntry> Candidates(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
    {
        if (!file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl)
            || !Uri.TryCreate(file.GalleryDownloadUrl.Value, UriKind.Absolute, out var recordedUri)) return [];

        var linked = listing.Entries.Where(e => e.Download is { } link && SameLink(link, recordedUri)).ToList();
        if (linked.Count == 0) return [];

        if (file.AsLibraryFile().XxHash64Identity() is { } hash)
        {
            var byHash = linked.Where(e => e.DownloadHash is { } offered && offered.Equals(hash)).ToList();
            if (byHash.Count > 0) return byHash;
        }

        if (!listing.IsComplete) return [];

        var game = WabbajackGames.GameIdFor(file.GameType);
        var inGame = linked.Where(e => game is null || e.Game == game).ToList();

        var fileName = file.AsLibraryFile().FileName.ToString();
        var byFileName = inGame.Where(e => string.Equals(WabbajackGalleryDownloader.FileNameFor(e), fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFileName.Count > 0) return byFileName;

        return inGame
            .Where(e => game is not null
                        && string.Equals(e.Title, file.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Author, file.Author, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // The downloader records entry.Download.ToString(). Scheme and host compare case-insensitively (Uri lower-cases
    // them); the path and query are exact.
    private static bool SameLink(Uri entryLink, Uri recorded)
        => entryLink.IsAbsoluteUri
           && Uri.Compare(entryLink, recorded, UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    private static bool HasLookalikeElsewhere(GalleryEntry candidate, GalleryListing listing)
        => listing.Entries.Any(e => !string.Equals(e.Repository, candidate.Repository, StringComparison.Ordinal)
                                    && (string.Equals(e.Title, candidate.Title, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(e.MachineUrl, candidate.MachineUrl, StringComparison.Ordinal)));

    /// <summary>Records the entry the user picked as the list's identity.</summary>
    public async Task Assign(WabbajackModlistLibraryFile.ReadOnly file, GalleryEntry entry)
    {
        using var tx = connection.BeginTransaction();
        Write(tx, file, entry);
        await tx.Commit();
    }

    private static void Write(ITransaction tx, WabbajackModlistLibraryFile.ReadOnly file, GalleryEntry entry)
    {
        tx.Add(file.Id, WabbajackModlistLibraryFile.MachineUrl, entry.MachineUrl);
        tx.Add(file.Id, WabbajackModlistLibraryFile.Repository, entry.Repository);
    }
}

/// <summary>Runs the backfill on the cached listing at start and after every gallery refresh.</summary>
public sealed class ModlistIdentityBackfillRunner(IWabbajackGalleryClient gallery, ModlistIdentityBackfill backfill, ILogger<ModlistIdentityBackfillRunner> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;
    private readonly CancellationTokenSource _closing = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null) throw new InvalidOperationException("The identity backfill is already running.");

        // On the thread pool: the scan must not run inside the fetch that emits, nor delay the hosted services after this one.
        _subscription = gallery.Refreshed.Subscribe(listing => _ = Task.Run(() => RunSafely(listing)));
        _ = Task.Run(() => gallery.GetCached() is { } cached ? RunSafely(cached) : Task.CompletedTask);
        return Task.CompletedTask;
    }

    private async Task RunSafely(GalleryListing listing)
    {
        try
        {
            await backfill.Run(listing, _closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Recording the gallery identity of downloaded modlists failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        _closing.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _closing.Dispose();
    }
}
