using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using ReactiveUI;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.UI.Sdk.Icons;
using R3;
using ReactiveUI.SourceGenerators;
using Disposable = System.Reactive.Disposables.Disposable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

[UsedImplicitly]
public partial class NexusModsViewModel : APageViewModel<INexusModsViewModel>, INexusModsViewModel
{
    private const int PageSize = 40;
    private static readonly TimeSpan SearchPause = TimeSpan.FromMilliseconds(400);

    private readonly IConnection _connection;
    private readonly INexusModsBrowser _browser;
    private readonly ILoginManager _loginManager;
    private readonly ILibraryService _libraryService;
    private readonly ILoadoutManager _loadoutManager;
    private readonly TemporaryFileManager _temporaryFiles;
    private readonly IOSInterop _osInterop;
    private readonly IGameDomainToGameIdMappingCache _domains;
    private readonly IEnumerable<IGameData> _games;
    private readonly ILogger<NexusModsViewModel> _logger;
    private readonly NexusCollectionsViewModel _collections;
    private readonly IStoreSortMemory _sortMemory;
    private readonly IAdultContentSetting _adultContent;

    // Bumped by every new search so a slow answer to an old one is dropped instead of overwriting the newer rows.
    private int _generation;

    [Reactive] public partial LoadoutId LoadoutId { get; set; }
    [Reactive] public partial string SearchText { get; set; } = "";
    [Reactive] public partial NexusModsSort Sort { get; set; } = NexusModsSort.Endorsements;
    [Reactive] public partial IReadOnlyList<NexusModsModRow> Mods { get; private set; } = [];
    [Reactive] public partial bool HasMore { get; private set; }
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial string ErrorText { get; private set; } = "";
    [Reactive] public partial string InstallButtonText { get; private set; } = "Download on Nexus Mods";

    public IThumbnailLoader Thumbnails { get; }
    public INexusCollectionsViewModel Collections => _collections;
    public ReactiveUI.ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveUI.ReactiveCommand<NexusModsModRow, Unit> InstallCommand { get; }
    public ReactiveUI.ReactiveCommand<NexusModsModRow, Unit> OpenCommand { get; }

    public NexusModsViewModel(
        IWindowManager windowManager,
        IConnection connection,
        INexusModsBrowser browser,
        ILoginManager loginManager,
        ILibraryService libraryService,
        ILoadoutManager loadoutManager,
        TemporaryFileManager temporaryFiles,
        IOSInterop osInterop,
        IGameDomainToGameIdMappingCache domains,
        IEnumerable<IGameData> games,
        IThumbnailLoader thumbnails,
        NexusCollectionsViewModel collections,
        IStoreSortMemory sortMemory,
        IAdultContentSetting adultContent,
        ILogger<NexusModsViewModel> logger) : base(windowManager)
    {
        _connection = connection;
        _browser = browser;
        _loginManager = loginManager;
        _libraryService = libraryService;
        _loadoutManager = loadoutManager;
        _temporaryFiles = temporaryFiles;
        _osInterop = osInterop;
        _games = games;
        _domains = domains;
        Thumbnails = thumbnails;
        _logger = logger;
        _collections = collections;
        _sortMemory = sortMemory;
        _adultContent = adultContent;

        TabIcon = IconValues.Download;
        TabTitle = "Nexus Mods";

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async ct => await LoadMoreAsync(ct));
        InstallCommand = ReactiveCommand.CreateFromTask<NexusModsModRow>(async (row, ct) => await InstallAsync(row, ct));
        OpenCommand = ReactiveCommand.Create<NexusModsModRow>(Open);

        this.WhenAnyValue(vm => vm.LoadoutId).Subscribe(loadoutId =>
        {
            _collections.LoadoutId = loadoutId;
            RestoreSort();
        });
        this.WhenAnyValue(vm => vm.Sort).Skip(1).Subscribe(RememberSort);

        this.WhenActivated(d =>
        {
            _collections.OpenPage = pageData =>
            {
                var workspaceController = GetWorkspaceController();
                workspaceController.OpenPage(WorkspaceId, pageData, workspaceController.GetDefaultOpenPageBehavior(pageData, NavigationInput.Default));
            };
            _collections.Start().DisposeWith(d);

            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(d);

            // The first emission is the current text and sort, so it loads the page on activation; later ones wait
            // out the typing.
            // The login replays its current state, so this also sets the text the page opens with.
            _loginManager.UserInfoObservable
                .Select(static user => user?.UserRole == UserRole.Premium ? "Install" : "Download on Nexus Mods")
                .ObserveOnUIThreadDispatcher()
                .Subscribe(text => InstallButtonText = text)
                .DisposeWith(d);

            // The adult setting joins the trigger: flipping it lists the page again, and replays its value on subscribing.
            this.WhenAnyValue(vm => vm.SearchText, vm => vm.Sort)
                .CombineLatest(_adultContent.Changes, static (search, adult) => (search, adult))
                .Throttle(SearchPause, RxApp.MainThreadScheduler)
                .Subscribe(change => { var _ = SearchSafely(cts.Token); })
                .DisposeWith(d);
        });
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
        await LoadPage(offset: Mods.Count, replace: false, _generation, cancellationToken);
    }

    private async Task LoadPage(int offset, bool replace, int generation, CancellationToken cancellationToken)
    {
        if (GameOfLoadout() is not { } game)
        {
            Mods = [];
            HasMore = false;
            ErrorText = "This game is not on Nexus Mods.";
            return;
        }

        IsLoading = true;
        try
        {
            var query = new NexusModsSearchQuery(game, SearchText.Trim(), Sort, offset, PageSize, _adultContent.ShowAdultContent);
            var page = await _browser.Search(query, cancellationToken);
            if (generation != _generation) return;

            var rows = page.Hits.Select(hit => new NexusModsModRow(hit));
            Mods = replace ? rows.ToArray() : Mods.Concat(rows).ToArray();
            HasMore = page.HasMore;
            ErrorText = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (generation != _generation) return;
            _logger.LogWarning(e, "Could not list the Nexus Mods catalog of game {Game}", game);
            // A failed later page keeps what is shown and the Load more button, so the user can ask for it again.
            if (replace)
            {
                Mods = [];
                HasMore = false;
            }
            ErrorText = $"Could not load mods from Nexus Mods: {e.Message}";
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private const string SortPage = "nexus-mods";

    // Remembered per Nexus game, so a loadout of the same game shows the same order.
    private void RestoreSort()
    {
        if (GameOfLoadout() is not { } game || _sortMemory.Recall(SortPage, game.ToString()) is not { } key) return;
        if (Enum.TryParse<NexusModsSort>(key, out var sort) && Enum.IsDefined(sort)) Sort = sort;
    }

    private void RememberSort(NexusModsSort sort)
    {
        if (GameOfLoadout() is { } game) _sortMemory.Remember(SortPage, game.ToString(), sort.ToString());
    }

    // The same rule as the left menu entry, so the entry and the page can't disagree.
    private NexusModsGameId? GameOfLoadout()
    {
        return _connection.Db.NexusIdOfLoadout(LoadoutId, _games);
    }

    private Uri ModUri(NexusModsGameId game, NexusModsModRow row) => NexusModsUrlBuilder.GetModUri(_domains.GetDomain(game), row.Hit.ModId);

    private Uri FilesTabUri(NexusModsGameId game, NexusModsModRow row) => new UriBuilder(ModUri(game, row)) { Query = "tab=files" }.Uri;

    public void Open(NexusModsModRow row)
    {
        if (GameOfLoadout() is { } game) _osInterop.OpenUri(ModUri(game, row));
    }

    // Mods being installed right now, so a second click on the same card waits for the first instead of downloading again.
    private readonly HashSet<(LoadoutId Loadout, ModId Mod)> _installing = [];

    public async Task InstallAsync(NexusModsModRow row, CancellationToken cancellationToken)
    {
        var key = (LoadoutId, row.Hit.ModId);
        if (!_installing.Add(key)) return;
        ErrorText = "";
        try
        {
            if (GameOfLoadout() is not { } game) throw new InvalidOperationException("This game is not on Nexus Mods.");

            // Free members can only download through the site, which hands out the key the API needs.
            if (!_loginManager.IsPremium || await _browser.FindMainFile(game, row.Hit.ModId, cancellationToken) is not { } fileId)
            {
                _osInterop.OpenUri(FilesTabUri(game, row));
                return;
            }

            var item = FindInLibrary(game, row.Hit.ModId, fileId) ?? await DownloadAsync(game, row, fileId, cancellationToken);
            var outcome = await ThunderstoreLoadoutPackages.InstallOrEnable(_connection, item, LoadoutId, async () => await _loadoutManager.InstallItem(item, LoadoutId));
            if (outcome == LoadoutPackageOutcome.AlreadyEnabled) ErrorText = $"{row.Name} is already in this loadout.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not install {Mod} from Nexus Mods", row.Name);
            ErrorText = $"Could not install {row.Name}: {e.Message}";
        }
        finally
        {
            _installing.Remove(key);
        }
    }

    // A file the library already holds is installed from there. Older downloads of the same file can sit in the library
    // more than once, so a copy that is already linked into this loadout wins (it may be enabled or just disabled);
    // otherwise any copy installs the same files.
    private LibraryItem.ReadOnly? FindInLibrary(NexusModsGameId game, ModId modId, FileId fileId)
    {
        LibraryItem.ReadOnly? first = null;
        foreach (var file in NexusModsFileMetadata.FindByUid(_connection.Db, new FileUid(fileId, game)))
        {
            if (file.ModPage.Uid.ModId != modId) continue;
            foreach (var found in NexusModsLibraryItem.FindByFileMetadata(_connection.Db, file))
            {
                var item = found.AsLibraryItem();
                if (IsLinkedIntoLoadout(item)) return item;
                first ??= item;
            }
        }

        return first;
    }

    private bool IsLinkedIntoLoadout(LibraryItem.ReadOnly item)
        => LibraryLinkedLoadoutItem.FindByLibraryItem(_connection.Db, item).Any(link => link.AsLoadoutItem().LoadoutId == LoadoutId);

    private async Task<LibraryItem.ReadOnly> DownloadAsync(NexusModsGameId game, NexusModsModRow row, FileId fileId, CancellationToken cancellationToken)
    {
        // The temporary file has to outlive AddDownload, which moves the finished file into the library's store.
        await using var destination = _temporaryFiles.CreateFile();
        var job = await _browser.CreateDownloadJob(game, row.Hit.ModId, fileId, destination, cancellationToken);
        var file = await _libraryService.AddDownload(job, UnassignedAcquisition.Instance);
        return file.AsLibraryItem();
    }
}
