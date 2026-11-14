using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Catalog;

[UsedImplicitly]
public partial class CatalogViewModel : APageViewModel<ICatalogViewModel>, ICatalogViewModel
{
    private static readonly TimeSpan SearchPause = TimeSpan.FromMilliseconds(400);

    private readonly ModCatalogs _catalogs;
    private readonly IConnection _connection;
    private readonly IStoreSortMemory _sortMemory;
    private readonly IAdultContentSetting _adultContent;
    private readonly IErrorPresenter _errors;
    private readonly IWindowNotificationService _notifications;
    private readonly IOSInterop _osInterop;
    private readonly ILogger<CatalogViewModel> _logger;

    private IModCatalog? _catalog;
    private IGameData? _game;
    private LoadoutId _loadoutId;
    private string? _nextCursor;

    // Bumped by every new search so a slow answer to an old one is dropped instead of overwriting the newer rows.
    private int _generation;

    [Reactive] public partial string CatalogName { get; private set; } = "";
    [Reactive] public partial string SearchText { get; set; } = "";
    [Reactive] public partial string SortKey { get; set; } = "";
    [Reactive] public partial IReadOnlyList<StoreSortOption> SortOptions { get; private set; } = [];
    [Reactive] public partial IReadOnlyList<CatalogCardRow> Rows { get; private set; } = [];
    [Reactive] public partial bool HasMore { get; private set; }
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial string ErrorText { get; private set; } = "";
    [Reactive] public partial string StatusText { get; private set; } = "";

    public IThumbnailLoader Icons { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<CatalogCardRow, Unit> InstallCommand { get; }
    public ReactiveCommand<CatalogCardRow, Unit> OpenCommand { get; }

    public CatalogViewModel(
        IWindowManager windowManager,
        ModCatalogs catalogs,
        IConnection connection,
        IThumbnailLoader icons,
        IStoreSortMemory sortMemory,
        IAdultContentSetting adultContent,
        IErrorPresenter errors,
        IWindowNotificationService notifications,
        IOSInterop osInterop,
        ILogger<CatalogViewModel> logger) : base(windowManager)
    {
        _catalogs = catalogs;
        _connection = connection;
        Icons = icons;
        _sortMemory = sortMemory;
        _adultContent = adultContent;
        _errors = errors;
        _notifications = notifications;
        _osInterop = osInterop;
        _logger = logger;

        TabIcon = IconValues.Download;

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async ct => await LoadMoreAsync(ct));
        InstallCommand = ReactiveCommand.CreateFromTask<CatalogCardRow>(InstallAsync);
        OpenCommand = ReactiveCommand.Create<CatalogCardRow>(row =>
        {
            if (row.Page is { } page) _osInterop.OpenUri(page);
        });

        this.WhenAnyValue(vm => vm.SortKey).Skip(1).Subscribe(RememberSort);

        this.WhenActivated(d =>
        {
            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(d);

            // The first emission is the current text and sort, so it loads the page on activation; later ones wait
            // out the typing. The adult setting joins the trigger: flipping it lists the page again.
            this.WhenAnyValue(vm => vm.SearchText, vm => vm.SortKey)
                .CombineLatest(_adultContent.Changes, static (search, adult) => (search, adult))
                .Throttle(SearchPause, RxApp.MainThreadScheduler)
                .Subscribe(_ => { var __ = SearchSafely(cts.Token); })
                .DisposeWith(d);
        });
    }

    /// <inheritdoc />
    public void Initialize(string catalog, LoadoutId loadoutId)
    {
        _loadoutId = loadoutId;
        _catalog = _catalogs.Named(catalog);
        CatalogName = catalog;
        TabTitle = catalog;

        var loadout = Loadout.Load(_connection.Db, loadoutId);
        _game = loadout.TryGetGame(out var game) ? game : null;

        if (_catalog is null) return;
        SortOptions = _catalog.Sorts.Select(sort => new StoreSortOption(sort.Key, sort.Label)).ToArray();
        SortKey = RememberedSort() ?? _catalog.Sorts[0].Key;
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
        await LoadPage(cursor: null, replace: true, generation, cancellationToken);
    }

    /// <summary>Appends the next page of the current search, if the catalog has one.</summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (!HasMore || IsLoading) return;
        await LoadPage(_nextCursor, replace: false, _generation, cancellationToken);
    }

    private async Task LoadPage(string? cursor, bool replace, int generation, CancellationToken cancellationToken)
    {
        if (_catalog is null || _game is null)
        {
            Rows = [];
            HasMore = false;
            StatusText = "";
            ErrorText = _catalog is null ? $"There is no catalog called {CatalogName}." : $"{CatalogName} has no mods for this game.";
            return;
        }

        IsLoading = true;
        try
        {
            var sort = _catalog.Sorts.FirstOrDefault(candidate => candidate.Key == SortKey) ?? _catalog.Sorts[0];
            var page = await _catalog.Search(_game, new CatalogQuery(SearchText.Trim(), sort, cursor, _adultContent.ShowAdultContent), cancellationToken);
            if (generation != _generation) return;

            var label = _catalog.InstallLabel(_game);
            var rows = page.Cards.Select(card => new CatalogCardRow(card, label));
            Rows = replace ? rows.ToArray() : Rows.Concat(rows).ToArray();
            _nextCursor = page.NextCursor;
            HasMore = page.NextCursor is not null;
            ErrorText = "";
            StatusText = page.FromCache ? $"{CatalogName} did not answer, so this is the list saved earlier." : "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (generation != _generation) return;
            _logger.LogWarning(e, "Could not list the {Catalog} catalog", CatalogName);
            // A failed later page keeps what is shown and the Load more button, so the user can ask for it again.
            if (replace)
            {
                Rows = [];
                HasMore = false;
            }

            StatusText = "";
            ErrorText = $"Could not load mods from {CatalogName}: {e.Message}";
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private async Task InstallAsync(CatalogCardRow row, CancellationToken cancellationToken)
    {
        if (_catalog is null) return;

        try
        {
            // The downloads are jobs, so the spine's download indicator shows their progress.
            switch (await _catalog.Install(row.Card, _loadoutId, cancellationToken))
            {
                case CatalogInstallOutcome.Installed installed:
                    _notifications.ShowToast(InstalledText(row, installed), ToastNotificationVariant.Success);
                    break;
                case CatalogInstallOutcome.Subscribed subscribed:
                    _notifications.ShowToast(subscribed.Note, ToastNotificationVariant.Success);
                    break;
                case CatalogInstallOutcome.OpenedOnWebsite opened:
                    _osInterop.OpenUri(opened.Page);
                    break;
                case CatalogInstallOutcome.Refused refused:
                    await _errors.Report(refused.Why, $"installing {row.Title}");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _errors.Report(e, $"installing {row.Title}");
        }
    }

    private static string InstalledText(CatalogCardRow row, CatalogInstallOutcome.Installed installed)
        => installed.PerPackage.Count > 0 && installed.PerPackage.All(outcome => outcome.Outcome == LoadoutPackageOutcome.AlreadyEnabled)
            ? $"{row.Title} is already in this loadout."
            : $"{row.Title} was added to this loadout.";

    private string? RememberedSort()
    {
        if (_catalog is null || _game is null || _sortMemory.Recall(_catalog.DisplayName, _game.GameId.ToString()) is not { } key) return null;
        // A key from a build with different sorts is ignored rather than guessed at.
        return _catalog.Sorts.Any(sort => sort.Key == key) ? key : null;
    }

    private void RememberSort(string key)
    {
        if (_catalog is not null && _game is not null && key != "") _sortMemory.Remember(_catalog.DisplayName, _game.GameId.ToString(), key);
    }
}
