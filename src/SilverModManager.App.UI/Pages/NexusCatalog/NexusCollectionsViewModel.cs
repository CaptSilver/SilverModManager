using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.CLI.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

[UsedImplicitly]
public partial class NexusCollectionsViewModel : ReactiveObject, INexusCollectionsViewModel
{
    private const int PageSize = 40;
    private static readonly TimeSpan SearchPause = TimeSpan.FromMilliseconds(400);

    private readonly IConnection _connection;
    private readonly INexusCollectionsBrowser _browser;
    private readonly IOSInterop _osInterop;
    private readonly IEnumerable<IGameData> _games;
    private readonly IEnumerable<IIpcProtocolHandler> _protocolHandlers;
    private readonly ILogger<NexusCollectionsViewModel> _logger;

    // Bumped by every new search so a slow answer to an old one is dropped instead of overwriting the newer rows.
    private int _generation;

    [Reactive] public partial string SearchText { get; set; } = "";
    [Reactive] public partial NexusCollectionsSort Sort { get; set; } = NexusCollectionsSort.Endorsements;
    [Reactive] public partial IReadOnlyList<NexusCollectionRow> Collections { get; private set; } = [];
    [Reactive] public partial bool HasMore { get; private set; }
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial string ErrorText { get; private set; } = "";
    [Reactive] public partial bool IsVisible { get; set; }

    /// <summary>The loadout whose game's collections are listed, and which an opened collection page installs into.</summary>
    public LoadoutId LoadoutId { get; set; }

    /// <summary>
    /// Opens a page in the workspace this tab lives in. The Nexus Mods page sets it, because only a page knows its workspace.
    /// </summary>
    public Action<PageData> OpenPage { get; set; } = _ => throw new InvalidOperationException("The page that hosts the Collections tab has not said where to open pages.");

    public IThumbnailLoader Thumbnails { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveUI.ReactiveCommand<NexusCollectionRow, Unit> PrimaryCommand { get; }
    public ReactiveUI.ReactiveCommand<NexusCollectionRow, Unit> OpenOnNexusCommand { get; }

    public NexusCollectionsViewModel(
        IConnection connection,
        INexusCollectionsBrowser browser,
        IOSInterop osInterop,
        IEnumerable<IGameData> games,
        IEnumerable<IIpcProtocolHandler> protocolHandlers,
        IThumbnailLoader thumbnails,
        ILogger<NexusCollectionsViewModel> logger)
    {
        _connection = connection;
        _browser = browser;
        _osInterop = osInterop;
        _games = games;
        _protocolHandlers = protocolHandlers;
        Thumbnails = thumbnails;
        _logger = logger;

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async ct => await LoadMoreAsync(ct));
        PrimaryCommand = ReactiveCommand.CreateFromTask<NexusCollectionRow>(async (row, ct) => await InstallAsync(row, ct));
        OpenOnNexusCommand = ReactiveCommand.Create<NexusCollectionRow>(OpenOnNexus);
    }

    /// <summary>
    /// Starts searching as the text and sort change, and following the library. The first search waits until the tab is
    /// shown; coming back to it later searches again only if the text or sort changed meanwhile.
    /// </summary>
    public IDisposable Start()
    {
        var disposables = new CompositeDisposable();
        var cts = new CancellationTokenSource();
        Disposable.Create(() =>
        {
            cts.Cancel();
            cts.Dispose();
        }).DisposeWith(disposables);

        this.WhenAnyValue(vm => vm.SearchText, vm => vm.Sort, vm => vm.IsVisible, (text, sort, visible) => (text, sort, visible))
            .Where(static state => state.visible)
            .Throttle(SearchPause, RxApp.MainThreadScheduler)
            .DistinctUntilChanged(static state => (state.text, state.sort))
            .Subscribe(_ => { var __ = SearchSafely(cts.Token); })
            .DisposeWith(disposables);

        CollectionRevisionMetadata.ObserveAll(_connection)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshLibraryState())
            .DisposeWith(disposables);

        return disposables;
    }

    // SearchAsync reports failures through ErrorText; leaving the page mid-request is the one exception it lets out.
    private async Task SearchSafely(CancellationToken cancellationToken)
    {
        try
        {
            await SearchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Replaces the list with the first page of the current search.</summary>
    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        await LoadPage(offset: 0, replace: true, generation, cancellationToken);
    }

    /// <summary>Appends the next page of the current search, if the catalog has one.</summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (!HasMore || IsLoading) return;
        await LoadPage(offset: Collections.Count, replace: false, _generation, cancellationToken);
    }

    private async Task LoadPage(int offset, bool replace, int generation, CancellationToken cancellationToken)
    {
        if (GameOfLoadout() is not { } game)
        {
            Collections = [];
            HasMore = false;
            ErrorText = "This game is not on Nexus Mods.";
            return;
        }

        IsLoading = true;
        try
        {
            var query = new NexusCollectionsSearchQuery(game, SearchText.Trim(), Sort, offset, PageSize);
            var page = await _browser.Search(query, cancellationToken);
            if (generation != _generation) return;

            var db = _connection.Db;
            var rows = page.Hits.Select(hit => new NexusCollectionRow(hit, LibraryRevisionOf(db, hit.Slug)));
            Collections = replace ? rows.ToArray() : Collections.Concat(rows).ToArray();
            HasMore = page.HasMore;
            ErrorText = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (generation != _generation) return;
            _logger.LogWarning(e, "Could not list the Nexus Mods collections of game {Game}", game);
            // A failed later page keeps what is shown, so scrolling to the end asks for it again.
            if (replace)
            {
                Collections = [];
                HasMore = false;
            }
            ErrorText = $"Could not load collections from Nexus Mods: {e.Message}";
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private NexusModsGameId? GameOfLoadout() => _connection.Db.NexusIdOfLoadout(LoadoutId, _games);

    // The newest revision the library holds: a collection added at an older revision still counts as being there.
    private static CollectionRevisionMetadataId? LibraryRevisionOf(IDb db, CollectionSlug slug)
    {
        var newest = CollectionMetadata.FindBySlug(db, slug)
            .SelectMany(collection => collection.Revisions)
            .OrderByDescending(revision => revision.RevisionNumber.Value)
            .Cast<CollectionRevisionMetadata.ReadOnly?>()
            .FirstOrDefault();
        return newest is { } revision ? revision : null;
    }

    private void RefreshLibraryState()
    {
        var db = _connection.Db;
        foreach (var row in Collections) row.LibraryRevision = LibraryRevisionOf(db, row.Hit.Slug);
    }

    public void OpenOnNexus(NexusCollectionRow row)
        => _osInterop.OpenUri(NexusModsUrlBuilder.GetCollectionUri(row.Hit.GameDomain, row.Hit.Slug, DynamicData.Kernel.Optional<RevisionNumber>.None));

    /// <summary>Opens the collection if the library holds it; otherwise adds its latest revision the way an nxm:// link does.</summary>
    public async Task InstallAsync(NexusCollectionRow row, CancellationToken cancellationToken)
    {
        ErrorText = "";
        try
        {
            if (row.LibraryRevision is { } inLibrary)
            {
                OpenPage(new PageData
                {
                    FactoryId = CollectionDownloadPageFactory.StaticId,
                    Context = new CollectionDownloadPageContext
                    {
                        TargetLoadout = LoadoutId,
                        CollectionRevisionMetadataId = inLibrary,
                    },
                });
                return;
            }

            if (row.Hit.LatestRevision is not { } revision)
                throw new InvalidOperationException("it has no published revision on Nexus Mods.");

            // The nxm handler is the one place that knows how to add a collection: login check, free versus Premium
            // download, the toasts and opening the download page. This goes through it rather than around it.
            var handler = _protocolHandlers.FirstOrDefault(static handler => handler.Protocol == "nxm")
                          ?? throw new InvalidOperationException("The nxm protocol handler is not registered.");
            await handler.Handle($"nxm://{row.Hit.GameDomain}/collections/{row.Hit.Slug}/revisions/{revision}", cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not add the collection {Collection} from Nexus Mods", row.Name);
            ErrorText = $"Could not add {row.Name}: {e.Message}";
        }
    }
}
