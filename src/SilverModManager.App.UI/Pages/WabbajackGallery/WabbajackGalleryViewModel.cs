using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

[UsedImplicitly]
public partial class WabbajackGalleryViewModel : APageViewModel<IWabbajackGalleryViewModel>, IWabbajackGalleryViewModel
{
    private const string SortPage = "wabbajack";

    private readonly IConnection _connection;
    private readonly IWabbajackGalleryService _gallery;
    private readonly ILogger<WabbajackGalleryViewModel> _logger;
    private readonly IStoreSortMemory _sortMemory;
    private readonly IAdultContentSetting _adultContent;
    private IReadOnlyList<GalleryEntry> _entries = [];
    private string? _game;

    [Reactive] public partial LoadoutId LoadoutId { get; set; }
    [Reactive] public partial string SearchText { get; set; } = "";
    [Reactive] public partial WabbajackGallerySort Sort { get; set; } = WabbajackGallerySort.Updated;
    [Reactive] public partial IReadOnlyList<WabbajackGalleryRow> Lists { get; private set; } = [];
    [Reactive] public partial string ErrorText { get; private set; } = "";
    [Reactive] public partial string RefreshedText { get; private set; } = "";
    [Reactive] public partial bool IsRefreshing { get; private set; }

    public IThumbnailLoader Thumbnails { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<WabbajackGalleryRow, Unit> InstallCommand { get; }
    public ReactiveCommand<Uri, Unit> OpenLinkCommand { get; }

    /// <summary>Opens a page in the workspace this page is in; tests swap it for a recorder.</summary>
    public Action<PageData> OpenPage { get; set; }

    public WabbajackGalleryViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IWabbajackGalleryService gallery,
        IOSInterop osInterop,
        IThumbnailLoader thumbnails,
        IStoreSortMemory sortMemory,
        IAdultContentSetting adultContent,
        ILogger<WabbajackGalleryViewModel> logger) : base(windowManager)
    {
        _connection = connection;
        _gallery = gallery;
        _logger = logger;
        Thumbnails = thumbnails;
        _sortMemory = sortMemory;
        _adultContent = adultContent;

        TabIcon = IconValues.Download;
        TabTitle = "Wabbajack";

        OpenPage = pageData =>
        {
            var workspaceController = GetWorkspaceController();
            workspaceController.OpenPage(WorkspaceId, pageData, workspaceController.GetDefaultOpenPageBehavior(pageData, NavigationInput.Default));
        };

        this.WhenAnyValue(vm => vm.SearchText, vm => vm.Sort).Subscribe(_ => ApplyFilter());
        this.WhenAnyValue(vm => vm.Sort).Skip(1).Subscribe(RememberSort);

        RefreshCommand = ReactiveCommand.CreateFromTask(async ct => await LoadAsync(ct, forceRefresh: true));
        InstallCommand = ReactiveCommand.CreateFromTask<WabbajackGalleryRow>(InstallAsync);
        OpenLinkCommand = ReactiveCommand.Create<Uri>(osInterop.OpenUri);

        this.WhenActivated(d =>
        {
            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(d);
            _ = LoadOnActivation(cts.Token);

            // The first value is the current one, which the load above already applied.
            _adultContent.Changes.Skip(1).OnUI().Subscribe(_ => ApplyFilter()).DisposeWith(d);
        });
    }

    // LoadAsync reports its failures through ErrorText; leaving the page mid-load is the one exception it lets out.
    private async Task LoadOnActivation(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Shows the gallery, from the cache when it is fresh enough.</summary>
    public Task LoadAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken, forceRefresh: false);

    private async Task LoadAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        RestoreSort(GameOfLoadout());

        IsRefreshing = true;
        try
        {
            var listing = await _gallery.GetListing(forceRefresh, cancellationToken);
            _entries = listing.Entries;
            RefreshedText = listing.IsStale
                ? $"Last refreshed {Format(listing.FetchedAt)}; the gallery could not be reached, so this is an offline copy"
                : $"Last refreshed {Format(listing.FetchedAt)}";
            ErrorText = "";
        }
        // Only this page's own cancellation is let out: anything else thrown out of a command takes the app down.
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // What was listed before stays: a failed refresh shouldn't empty the page.
            _logger.LogWarning(e, "Could not read the Wabbajack gallery");
            ErrorText = e is GalleryUnavailableException ? e.Message : $"Could not read the Wabbajack gallery: {e.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }

        ApplyFilter();
    }

    private static string Format(DateTimeOffset time) => time.ToLocalTime().ToString("d MMM HH:mm");

    // By the installation's recorded id, not the game registry: the registry throws for an installation it hasn't
    // located, and a page shouldn't fail because its game went missing.
    private GameId? GameOfLoadout()
    {
        if (LoadoutId == default) return null;
        var loadout = Loadout.Load(_connection.Db, LoadoutId);
        return loadout.IsValid() && GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId) ? gameId : null;
    }

    private async Task InstallAsync(WabbajackGalleryRow row, CancellationToken cancellationToken)
    {
        ErrorText = "";
        try
        {
            var file = row.LibraryFile;
            if (file is null)
            {
                file = await _gallery.Download(row.Entry, cancellationToken);
                // The row reads Open from now on.
                ApplyFilter();
            }

            OpenPage(new PageData
            {
                FactoryId = WabbajackModlistPageFactory.StaticId,
                Context = new WabbajackModlistPageContext(file.Value),
            });
        }
        // The download job can be cancelled from the jobs panel, which this page's token knows nothing about.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorText = $"Downloading {row.Title} was cancelled";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not install {Title} from the Wabbajack gallery", row.Title);
            ErrorText = $"Could not install {row.Title}: {e.Message}";
        }
    }

    // Remembered per game, which is what the page lists.
    private void RestoreSort(GameId? game)
    {
        _game = game?.ToString();
        if (_game is null || _sortMemory.Recall(SortPage, _game) is not { } key) return;
        // A key from a build with different sorts is ignored rather than guessed at.
        if (Enum.TryParse<WabbajackGallerySort>(key, out var sort) && Enum.IsDefined(sort)) Sort = sort;
    }

    private void RememberSort(WabbajackGallerySort sort)
    {
        if (_game is not null) _sortMemory.Remember(SortPage, _game, sort.ToString());
    }

    private void ApplyFilter()
    {
        var game = GameOfLoadout();
        if (game is null)
        {
            Lists = [];
            return;
        }

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inLibrary = _gallery.ListsInLibrary();
        var shown = _entries.ForGame(game.Value).Visible(_adultContent.ShowAdultContent).Where(entry => terms.All(term => Matches(entry, term)));
        Lists = WabbajackGallerySorting.Order(shown, Sort)
            .Select(entry => new WabbajackGalleryRow(entry, entry.Download is { } url && inLibrary.TryGetValue(url.ToString(), out var file) ? file : null))
            .ToArray();
    }

    private static bool Matches(GalleryEntry entry, string term)
        => entry.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || entry.Author.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || entry.Tags.Any(tag => tag.Contains(term, StringComparison.CurrentCultureIgnoreCase));
}
