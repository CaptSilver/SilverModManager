using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from the asset of a GitHub release: the loaders and tools on a game's shelf, and me3.
/// </summary>
/// <remarks>
/// The update check asks GitHub once per repository, on entering the shelf and on Refresh, and keeps the answer for an
/// hour. It never opens a browser: the asset is fetched directly, so Update All has nothing to refuse.
/// </remarks>
internal sealed class GitHubModSource(
    GitHubReleases releases,
    GitHubShelfEntries entries,
    IConnection connection,
    IGitHubApi api,
    Func<HttpClient> client,
    IServiceProvider provider,
    ILogger<GitHubModSource> logger) : IModSource
{
    public string DisplayName => "GitHub";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item.Id, out _);

    public bool Owns(ModUpdate update) => update is GitHubModUpdate;

    public Uri? PageUri(LibraryItem.ReadOnly item) => TagPage(item);

    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => TagPage(item);

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        foreach (var repository in entries.For(game))
        {
            try
            {
                await releases.Latest(repository, force, cancellationToken);
            }
            catch (UserFacingException e)
            {
                // A used-up request budget is not the user's to fix mid-check; what was known stays.
                logger.LogWarning("Not checking {Repository} for a newer release: {Reason}", repository.Name, e.Message);
            }
        }

        releases.NotifyChanged();
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
        => releases.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor([item.Id])));

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Select(static item => item.Id).ToArray();
        return releases.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor(ids)));
    }

    // One update per repository, the way one Thunderstore package is one update.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
    {
        var shelf = entries.For(game).Select(static entry => entry.Key).ToHashSet();
        var ids = GitHubReleaseLibraryFile.All(connection.Db)
            .Where(file => entries.Find(file) is { } entry && shelf.Contains(entry.Key))
            .Select(static file => file.Id);
        return [.. Updates(ids)];
    }

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game)
        => releases.Changed.StartWith(Unit.Default).Select(_ => AllUpdates(game).Count > 0).DistinctUntilChanged();

    // Both a replace and a keep-old download; only a replace changes what a loadout links.
    public bool WillReplace(ModUpdateRequest request) => request.Replace;

    public async ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        // Resolved here rather than injected: injecting it forms a cycle when the sources are constructed.
        var batch = new ModUpdateBatch(request, provider.GetRequiredService<ILibraryService>(), cancellationToken);
        return await batch.Run(request.Updates.OfType<GitHubModUpdate>().Select(update => new Step(this, connection, update)));
    }

    // Hiding records the newest tag the user saw and passed on: a release newer than that is offered again.
    public async ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction();
        var changed = false;
        foreach (var item in items)
        {
            if (!TryGetFile(item.Id, out var file)) continue;

            var hiddenTag = GitHubReleaseLibraryFile.HiddenUpdateTag.TryGetValue(file, out var recorded) ? recorded : null;
            if (hidden)
            {
                if (entries.Find(file) is not { } entry || releases.Peek(entry) is not { } newest || hiddenTag == newest.TagName) continue;

                if (hiddenTag is not null) tx.Retract(file.Id, GitHubReleaseLibraryFile.HiddenUpdateTag, hiddenTag);
                tx.Add(file.Id, GitHubReleaseLibraryFile.HiddenUpdateTag, newest.TagName);
                changed = true;
            }
            else if (hiddenTag is not null)
            {
                tx.Retract(file.Id, GitHubReleaseLibraryFile.HiddenUpdateTag, hiddenTag);
                changed = true;
            }
        }

        if (!changed) return;

        await tx.Commit();
        releases.NotifyChanged();
    }

    // Always enabled for an item of this source: a row whose update is hidden has none, and tying the action to one
    // would take away the only way to show it again.
    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Where(item => TryGetFile(item.Id, out _)).Select(static item => item.Id).ToArray();
        if (ids.Length == 0) return Observable.Return(new HideUpdatesState(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false));

        return releases.Changed.StartWith(Unit.Default)
            .Select(_ => new HideUpdatesState(ids.All(IsHidden), ids.Length, IsEnabled: true, IsVisible: true))
            .DistinctUntilChanged();
    }

    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Show : HideUpdatesLabelKind.Hide;

    private bool TryGetFile(EntityId id, out GitHubReleaseLibraryFile.ReadOnly file)
    {
        file = GitHubReleaseLibraryFile.Load(connection.Db, id);
        return file.IsValid();
    }

    private bool IsHidden(EntityId id)
    {
        if (!TryGetFile(id, out var file) || !GitHubReleaseLibraryFile.HiddenUpdateTag.TryGetValue(file, out var hiddenTag)) return false;
        return entries.Find(file) is not { } entry || releases.Peek(entry) is not { } newest || NotNewerThan(newest.TagName, hiddenTag);
    }

    // Only a tag that is a version can be called newer; a nightly's name decides nothing.
    private static bool NotNewerThan(string tag, string reference)
        => !ReleaseVersion.TryParse(tag, out var version) || !ReleaseVersion.TryParse(reference, out var other) || version <= other;

    private static Optional<ModUpdate> AsOptional(GitHubModUpdate? update) => update is null ? Optional<ModUpdate>.None : Optional<ModUpdate>.Create(update);

    private GitHubModUpdate? UpdateFor(IReadOnlyList<EntityId> ids) => Updates(ids).FirstOrDefault();

    private IEnumerable<GitHubModUpdate> Updates(IEnumerable<EntityId> ids)
    {
        var byRepository = new Dictionary<string, (ShelfRepository Entry, Release Newest, List<(LibraryItem.ReadOnly Item, string Tag)> Items)>();
        foreach (var id in ids)
        {
            if (!TryGetFile(id, out var file) || entries.Find(file) is not { } entry || releases.Peek(entry) is not { } newest) continue;
            if (!IsNewer(file, newest)) continue;

            if (!byRepository.TryGetValue(entry.Key, out var group)) byRepository[entry.Key] = group = (entry, newest, []);
            group.Items.Add((LibraryItem.Load(connection.Db, id), file.Tag));
        }

        foreach (var (entry, newest, items) in byRepository.Values)
        {
            if (GitHubModUpdate.From(entry, newest, items) is { } update) yield return update;
        }
    }

    private static bool IsNewer(GitHubReleaseLibraryFile.ReadOnly file, Release newest)
    {
        if (NotNewerThan(newest.TagName, file.Tag)) return false;

        // Hiding passes on the release the user saw, not on everything that follows it.
        return !GitHubReleaseLibraryFile.HiddenUpdateTag.TryGetValue(file, out var hiddenTag) || !NotNewerThan(newest.TagName, hiddenTag);
    }

    private sealed class Step(GitHubModSource source, IConnection connection, GitHubModUpdate update) : DownloadThenReplaceStep(update, update.Repository.Name)
    {
        protected override IReadOnlyList<LibraryItem.ReadOnly> LoadCurrent() => LoadItems(connection, update);

        protected override async Task<LibraryItem.ReadOnly?> Download(IReadOnlyList<LibraryItem.ReadOnly> current, ModUpdateBatch batch)
            => await source.Download(update, current, batch.CancellationToken);
    }

    private async Task<LibraryItem.ReadOnly> Download(GitHubModUpdate update, IReadOnlyList<LibraryItem.ReadOnly> current, CancellationToken cancellationToken)
    {
        var entry = update.Repository;
        var sidecar = await ReleaseSidecars.Read(client(), update.Newest, logger, cancellationToken);
        var job = AddGitHubReleaseAssetJob.Create(
            provider, api, client(),
            GitHubRepositoryRef.FromName(entry.Owner, entry.Repository), update.Newest.TagName, entry.AssetSuffix,
            new UpdateAcquisition(current.Select(static item => LibraryItemId.From(item.Id))), entry.NameHint,
            expectedSha256: null, requireAdvertisedDigest: false, sidecar, releases.IdOf(entry));

        var monitor = provider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>();
        await using var registration = cancellationToken.Register(() => monitor.Cancel(job));
        return (await job).AsLibraryFile().AsLibraryItem();
    }

    private Uri? TagPage(LibraryItem.ReadOnly item)
    {
        if (!TryGetFile(item.Id, out var file)) return null;

        var owner = GitHubReleaseLibraryFile.Organization.TryGetValue(file, out var org) ? org : null;
        var name = GitHubReleaseLibraryFile.Repository.TryGetValue(file, out var repo) ? repo : null;
        if (owner is null || name is null)
        {
            if (entries.Find(file) is not { } entry) return null;
            (owner, name) = (entry.Owner, entry.Repository);
        }

        return new Uri($"https://github.com/{owner}/{name}/releases/tag/{Uri.EscapeDataString(file.Tag)}");
    }
}
