using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Gives a list downloaded before the library recorded gallery identities its <c>MachineUrl</c> and
/// <c>Repository</c>. A file added from disk (no download address) is never matched, and neither is one that already
/// has an identity. A listing missing a repository binds nothing: the lookalike check cannot see a same-titled or
/// same-MachineUrl entry in the repository that failed to load, so even an exact link, hash and title match might be
/// the survivor of an ambiguity a complete listing would have shown. The user can still pick with <see cref="Assign"/>.
/// The gallery lists third-party repositories, so a repository other than the one the file came from must not be able
/// to claim it. An entry is only a candidate when its own download link is the one the file was fetched from
/// (<see cref="WabbajackModlistLibraryFile.GalleryDownloadUrl"/>), its title is the list's own name, and no other
/// repository offers a list with the same title or machine URL. The link only proves which bytes were fetched, not who
/// lists them, which is why the title is checked too.
/// Every release of a list gets a new link, so a list that has moved on since the download, the case where an update
/// matters, has no entry with the recorded link. Then an entry whose link is another release of the same file (same
/// host, directory and file name, differing in the "_&lt;release&gt;" suffix) is a candidate, provided its title and
/// author are the file's and it is for the file's game. Without the exact link nothing else pins the list down, so
/// title and author are part of the candidate rule rather than a later check. The suffix must be a GUID, the form
/// the CDN gives, so an arbitrary "_name" is not taken for a release. A renewed link is also a weaker anchor than the
/// exact one: it says only that some entry reuses the file's name, and any repository in the gallery can publish such
/// an entry once the real list has left it. So the automatic pass binds by a renewed link only to an entry of a
/// repository Wabbajack runs (<see cref="GalleryEntry.IsFromWabbajackRepository"/>); the same entry from a
/// third-party repository is only offered to the user. Any candidate the automatic rule declines is still offered,
/// together with the lookalikes that stopped it, and the user picks with <see cref="Assign"/>.
/// </summary>
public sealed class ModlistIdentityBackfill(IConnection connection, ILogger<ModlistIdentityBackfill> logger)
{
    // One at a time: a refresh and the start-up pass must not both write the same file.
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>
    /// Writes the identity of every file with exactly one candidate. Never writes <c>GalleryDateUpdated</c>, and
    /// writes nothing at all when the listing is incomplete.
    /// </summary>
    public async Task Run(GalleryListing listing, CancellationToken ct)
    {
        if (!listing.IsComplete) return;

        await _running.WaitAsync(ct);
        try
        {
            var db = connection.Db;
            using var tx = connection.BeginTransaction();
            var assigned = 0;
            foreach (var file in WabbajackModlistLibraryFile.All(db))
            {
                if (!file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl) || file.Contains(WabbajackModlistLibraryFile.MachineUrl)) continue;
                if (AutomaticMatch(file, listing) is not { } match) continue;

                Write(tx, file, match);
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
    /// The entry <see cref="Run"/> binds the file to, or null when it binds none: there must be exactly one candidate,
    /// its title must be the list's own name, and no other repository may offer a list with that title or machine URL.
    /// An entry found only by a renewed link must also be from a repository Wabbajack runs.
    /// </summary>
    public GalleryEntry? AutomaticMatch(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
    {
        var (found, byRenewedRelease) = Search(file, listing);
        if (found is not [var only]) return null;

        if (!string.Equals(only.Title, file.Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (byRenewedRelease && !only.IsFromWabbajackRepository) return null;
        return Lookalikes(only, listing).Any() ? null : only;
    }

    /// <summary>
    /// The entries the file may be, from the most certain rule that finds any: the same bytes, the file name the
    /// downloader gives that entry, then the same title and author in the same game. Every rule only considers entries
    /// whose download link is the one the file was fetched from (<see cref="WabbajackModlistLibraryFile.GalleryDownloadUrl"/>),
    /// so a hash advertised by another repository's entry cannot claim the file. When none of those rules finds an
    /// entry, the renewed releases of the list are used instead. More than one is ambiguous and
    /// is left for the user to pick. On an incomplete listing nothing is offered, so the result never rests on a
    /// repository that could not be read (<see cref="Run"/> does not use it then either).
    /// </summary>
    public IReadOnlyList<GalleryEntry> Candidates(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
        => Search(file, listing).Entries;

    // ByRenewedRelease tells the automatic pass which rule found the entries, since only the exact link is trusted
    // whatever repository the entry is in.
    private readonly record struct Found(IReadOnlyList<GalleryEntry> Entries, bool ByRenewedRelease);

    private static Found Search(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
    {
        if (!listing.IsComplete
            || !file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl)
            || !Uri.TryCreate(file.GalleryDownloadUrl.Value, UriKind.Absolute, out var recordedUri)) return new Found([], false);

        var byLink = LinkedCandidates(file, listing, recordedUri);
        return byLink.Count > 0 ? new Found(byLink, false) : new Found(RenewedReleases(file, listing, recordedUri), true);
    }

    /// <summary>
    /// What the picker offers: <see cref="Candidates"/> plus the renewed releases of the list. The recorded link can
    /// belong to a third-party entry that copied the old link while the real list moved on; then the linked rules
    /// find only that entry, and the user must also see the real one to choose it. A lone candidate comes with the
    /// lookalikes in other repositories (same title or machine URL), the entries that stopped the automatic pass, so
    /// the user sees the real list next to a mimic instead of a single entry to confirm.
    /// </summary>
    public IReadOnlyList<GalleryEntry> PickerCandidates(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing)
    {
        var candidates = Candidates(file, listing);
        if (candidates.Count == 0
            || !Uri.TryCreate(file.GalleryDownloadUrl.Value, UriKind.Absolute, out var recordedUri)) return candidates;

        var all = candidates.Concat(RenewedReleases(file, listing, recordedUri).Where(e => !candidates.Contains(e))).ToList();
        return all is [var only] ? [only, .. Lookalikes(only, listing)] : all;
    }

    private static List<GalleryEntry> LinkedCandidates(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing, Uri recordedUri)
    {
        var linked = listing.Entries.Where(e => e.Download is { } link && SameLink(link, recordedUri)).ToList();
        if (linked.Count == 0) return [];

        if (file.AsLibraryFile().XxHash64Identity() is { } hash)
        {
            var byHash = linked.Where(e => e.DownloadHash is { } offered && offered.Equals(hash)).ToList();
            if (byHash.Count > 0) return byHash;
        }

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

    // Every Wabbajack release gets a new link (a "_<uuid>" suffix on the same file name), so a list that has moved on
    // since the download has no entry with the recorded link. Without the exact link nothing proves the entry is
    // the list, so the entry must be a release of the same file on the same host and carry the file's own game,
    // title and author.
    private static List<GalleryEntry> RenewedReleases(WabbajackModlistLibraryFile.ReadOnly file, GalleryListing listing, Uri recordedUri)
    {
        var game = WabbajackGames.GameIdFor(file.GameType);
        if (game is null || ReleaseStem(recordedUri) is not { } stem) return [];

        return listing.Entries
            .Where(e => e.Game == game
                        && string.Equals(e.Title, file.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Author, file.Author, StringComparison.OrdinalIgnoreCase)
                        && e.Download is { IsAbsoluteUri: true } link
                        && ReleaseStem(link) == stem)
            .ToList();
    }

    private readonly record struct Stem(string Scheme, string Host, string Directory, string Name);

    // The link without its release suffix, or null when it has none. The suffix is what follows the last "_" of the
    // last segment and must be a GUID, and what precedes it must end in ".wabbajack", so an unrelated underscore in a
    // name is not taken for one. Path case is exact, as in SameLink.
    private static Stem? ReleaseStem(Uri link)
    {
        var path = link.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var slash = path.LastIndexOf('/');
        var directory = path[..(slash + 1)];
        var last = path[(slash + 1)..];
        var underscore = last.LastIndexOf('_');
        // The CDN's suffix is a GUID in its hyphenated form; anything else after an underscore is part of a name.
        if (underscore < 0 || !Guid.TryParseExact(last[(underscore + 1)..], "D", out _)) return null;

        var name = last[..underscore];
        if (!name.EndsWith(".wabbajack", StringComparison.OrdinalIgnoreCase)) return null;
        return new Stem(link.Scheme, WabbajackCdnDownloadJob.CanonicalHost(link), directory, name);
    }

    // The downloader records entry.Download.ToString(). Scheme and host compare case-insensitively (Uri lower-cases
    // them); the path and query are exact.
    private static bool SameLink(Uri entryLink, Uri recorded)
        => entryLink.IsAbsoluteUri
           && Uri.Compare(entryLink, recorded, UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    private static IEnumerable<GalleryEntry> Lookalikes(GalleryEntry candidate, GalleryListing listing)
        => listing.Entries.Where(e => !string.Equals(e.Repository, candidate.Repository, StringComparison.Ordinal)
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
