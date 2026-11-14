using System.Reactive;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// The newest release of each shelf repository, kept for an hour so the shelf page, the update check and a Refresh
/// share one request per repository. GitHub lets an anonymous address make 60 requests an hour and the app's own
/// update check and the me3 download draw on the same budget, so nothing here asks on a timer.
/// </summary>
internal sealed class GitHubReleases(IGitHubApi api, GitHubShelfEntries entries, TimeProvider time, ILogger<GitHubReleases> logger)
{
    /// <summary>How old a kept release may get before the next visit to the shelf asks again.</summary>
    public static readonly TimeSpan RefreshAge = TimeSpan.FromHours(1);

    private sealed record Kept(Release Release, DateTimeOffset At);

    private readonly Dictionary<string, Kept> _kept = [];
    private readonly Dictionary<string, ulong> _ids = [];
    private readonly System.Threading.Lock _lock = new();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private readonly Subject<Unit> _changed = new();

    /// <summary>Emits after the releases changed, which is when an update that was not there may be.</summary>
    public IObservable<Unit> Changed => _changed;

    /// <summary>Tells observers to look again, after a check that asked about several repositories.</summary>
    public void NotifyChanged() => _changed.OnNext(Unit.Default);

    /// <summary>
    /// The release the update check and the shelf compare against, without asking GitHub: what was kept, however old.
    /// Null before the first answer.
    /// </summary>
    public Release? Peek(ShelfRepository repository)
    {
        // The same repository can sit on two games' shelves with different prerelease settings, and a library file
        // answers to whichever shelf found it; the answer kept under the other setting is better than none.
        lock (_lock)
        {
            if (_kept.TryGetValue(KeyOf(repository), out var kept)) return kept.Release;
            return _kept.TryGetValue(KeyOf(repository with { IncludePrereleases = !repository.IncludePrereleases }), out var other) ? other.Release : null;
        }
    }

    /// <summary>The repository's numeric id: the entry's, or the one learned from the first answer.</summary>
    public ulong? IdOf(ShelfRepository repository)
    {
        if (repository.RepositoryId is { } known) return known;
        lock (_lock) return _ids.TryGetValue(repository.Key, out var learned) ? learned : null;
    }

    /// <summary>
    /// The newest release: GitHub's marked latest, or the highest tag including prereleases for an entry that opted
    /// in. A kept answer is reused until it is an hour old, or always-asked again when <paramref name="force"/>. A
    /// failed request returns the kept answer, or null when there is none.
    /// </summary>
    /// <exception cref="GitHubRateLimitedException">GitHub has no requests left for now.</exception>
    public async ValueTask<Release?> Latest(ShelfRepository repository, bool force, CancellationToken cancellationToken)
    {
        var key = KeyOf(repository);
        if (!force && Fresh(key) is { } fresh) return fresh;

        await _fetching.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have asked while this one waited.
            if (!force && Fresh(key) is { } meanwhile) return meanwhile;

            var release = await Fetch(repository, cancellationToken);
            if (release is null) return Peek(repository);

            lock (_lock) _kept[key] = new Kept(release, time.GetUtcNow());
            await LearnId(repository, cancellationToken);
            return release;
        }
        finally
        {
            _fetching.Release();
        }
    }

    private Release? Fresh(string key)
    {
        lock (_lock) return _kept.TryGetValue(key, out var kept) && time.GetUtcNow() - kept.At < RefreshAge ? kept.Release : null;
    }

    private static string KeyOf(ShelfRepository repository) => repository.IncludePrereleases ? repository.Key + "+pre" : repository.Key;

    private async ValueTask<Release?> Fetch(ShelfRepository repository, CancellationToken cancellationToken)
    {
        if (repository.IncludePrereleases)
        {
            // The marked latest is never a prerelease, so the list is the only way to see one.
            var releases = await api.FetchReleases(repository.Owner, repository.Repository, cancellationToken);
            return releases?.Where(release => !release.IsDraft && ReleaseVersion.TryParse(release.TagName, out _))
                .MaxBy(release => ReleaseVersion.TryParse(release.TagName, out var version) ? version : null);
        }

        return IdOf(repository) is { } id
            ? await api.FetchLatestReleaseById(checked((long)id), cancellationToken)
            : await api.FetchMarkedLatestRelease(repository.Owner, repository.Repository, cancellationToken);
    }

    // The id is what lets a later check follow the repository through a rename, so it is recorded once on the first answer.
    private async ValueTask LearnId(ShelfRepository repository, CancellationToken cancellationToken)
    {
        if (IdOf(repository) is not null) return;

        var id = await api.FetchRepositoryId(repository.Owner, repository.Repository, cancellationToken);
        if (id is not { } learned)
        {
            logger.LogDebug("GitHub gave no id for {Repository}; it will be asked for again", repository.Name);
            return;
        }

        lock (_lock) _ids[repository.Key] = learned;
        await entries.Remember(repository, learned);
    }
}
