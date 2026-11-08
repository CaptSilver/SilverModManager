using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Gives a list downloaded before the library recorded gallery identities its <c>MachineUrl</c> and
/// <c>Repository</c>. Matching the old download address does not work: the gallery lists the newest release's address,
/// which is exactly when the installed one needs an identity. A file added from disk (no download address) is never
/// matched, and neither is one that already has an identity. A listing missing a repository only confirms exact hash
/// matches: a name or title match might be the survivor of an ambiguity the missing repository would have shown.
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
    /// downloader gives that entry, then the same title and author in the same game. More than one is ambiguous and
    /// is left for the user to pick. The name rules are skipped on an incomplete listing.
    /// </summary>
    public IReadOnlyList<GalleryEntry> Candidates(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
    {
        if (file.AsLibraryFile().XxHash64Identity() is { } hash)
        {
            var byHash = listing.Entries.Where(e => e.DownloadHash is { } offered && offered.Equals(hash)).ToList();
            if (byHash.Count > 0) return byHash;
        }

        if (!listing.IsComplete) return [];

        var game = WabbajackGames.GameIdFor(file.GameType);
        var inGame = listing.Entries.Where(e => game is null || e.Game == game).ToList();

        var fileName = file.AsLibraryFile().FileName.ToString();
        var byFileName = inGame.Where(e => string.Equals(WabbajackGalleryDownloader.FileNameFor(e), fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFileName.Count > 0) return byFileName;

        return inGame
            .Where(e => game is not null
                        && string.Equals(e.Title, file.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Author, file.Author, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

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
