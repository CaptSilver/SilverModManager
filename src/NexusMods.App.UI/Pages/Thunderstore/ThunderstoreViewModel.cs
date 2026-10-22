using System.Reactive;
using System.Reactive.Disposables;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Thunderstore;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Thunderstore;

[UsedImplicitly]
public class ThunderstoreViewModel : APageViewModel<IThunderstoreViewModel>, IThunderstoreViewModel
{
    private readonly IConnection _connection;
    private readonly IEnumerable<IGameData> _games;
    private readonly IThunderstoreCatalogSource _catalogs;
    private readonly IThunderstoreSource _source;
    private readonly ILoadoutManager _loadoutManager;
    private readonly ILogger<ThunderstoreViewModel> _logger;
    private ThunderstoreCatalog? _catalog;

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public bool ShowDeprecated { get; set; }
    [Reactive] public IReadOnlyList<ThunderstorePackageRow> Packages { get; private set; } = [];
    [Reactive] public string ErrorText { get; private set; } = "";
    [Reactive] public bool IsRefreshing { get; private set; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; }
    public ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommand { get; }

    public ThunderstoreViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IEnumerable<IGameData> games,
        IThunderstoreCatalogSource catalogs,
        IThunderstoreSource source,
        ILoadoutManager loadoutManager,
        IOSInterop osInterop,
        ILogger<ThunderstoreViewModel> logger) : base(windowManager)
    {
        _connection = connection;
        _games = games;
        _catalogs = catalogs;
        _source = source;
        _loadoutManager = loadoutManager;
        _logger = logger;

        TabIcon = IconValues.Download;
        TabTitle = "Thunderstore";

        this.WhenAnyValue(vm => vm.SearchText, vm => vm.ShowDeprecated).Subscribe(_ => ApplyFilter());

        RefreshCommand = ReactiveCommand.CreateFromTask(async ct => await LoadAsync(ct, forceRefresh: true));
        OpenPackageCommand = ReactiveCommand.Create<ThunderstorePackageRow>(row =>
        {
            if (row.PackageUrl is { } url) osInterop.OpenUri(url);
        });

        InstallCommand = ReactiveCommand.CreateFromTask<ThunderstorePackageRow>(InstallAsync);

        this.WhenActivated(d =>
        {
            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(d);
            _ = LoadOnActivation(cts.Token);
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

    /// <summary>
    /// Shows the cached catalog of the loadout's community, refreshing it first when it is stale.
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken, forceRefresh: false);

    private async Task LoadAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        var community = CommunityOfLoadout();
        if (community is null)
        {
            _catalog = null;
            ErrorText = "This game's mods do not come from Thunderstore.";
            ApplyFilter();
            return;
        }

        IsRefreshing = true;
        try
        {
            // The staleness check reads the cached index from disk, so a corrupt index fails here, not in Load.
            var error = await TryAsync(async () =>
            {
                if (forceRefresh || _catalogs.IsStale(community)) await _catalogs.Refresh(community, cancellationToken);
            }, "refresh", community);

            try
            {
                _catalog = await _catalogs.Load(community, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not load the Thunderstore catalog of {Community}", community);
                _catalog = null;
                // A failed refresh is the likelier cause of a missing catalog, so it keeps its own message.
                if (error == "") error = e.Message;
            }

            ErrorText = error;
            ApplyFilter();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task<string> TryAsync(Func<Task> action, string what, string community)
    {
        try
        {
            await action();
            return "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not {What} the Thunderstore index of {Community}", what, community);
            return $"Could not {what} the package index: {e.Message}";
        }
    }

    // The game comes from the installation's recorded id, not the game registry: the registry throws for an
    // installation it hasn't located, and a page shouldn't fail because its game went missing.
    private IThunderstoreGame? ThunderstoreGameOfLoadout()
    {
        if (LoadoutId == default) return null;
        var loadout = Loadout.Load(_connection.Db, LoadoutId);
        if (!GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId)) return null;
        var game = _games.FirstOrDefault(game => game.GameId == gameId);
        return game is null ? null : ThunderstoreGame.For(game);
    }

    private string? CommunityOfLoadout() => ThunderstoreGameOfLoadout()?.ThunderstoreCommunity;

    private async Task InstallAsync(ThunderstorePackageRow row, CancellationToken cancellationToken)
    {
        ErrorText = "";
        try
        {
            if (ThunderstoreGameOfLoadout() is not { } game) throw new InvalidOperationException("This game's mods do not come from Thunderstore.");
            if (row.Latest is not { } root) throw new InvalidOperationException($"{row.FullName} has no version to install.");

            // A modpack lists the exact versions it was tested with; anything else follows the newest ones.
            var isModpack = _catalog?.Categories(root.Package).Contains("Modpacks") ?? false;
            var request = new ThunderstoreInstallRequest(
                root,
                game,
                WithDependencies: true,
                isModpack ? DependencyPolicy.Exact : DependencyPolicy.Latest,
                ExpectedSha256: null,
                ThunderstoreLoadoutPackages.Installed(_connection.Db, LoadoutId));

            // The downloads are jobs, so the spine's download indicator shows their progress.
            var files = await _source.AddAsync(request, cancellationToken);
            foreach (var file in files)
            {
                var item = file.AsLibraryItem();
                if (ThunderstoreLoadoutPackages.IsLinked(_connection.Db, item, LoadoutId)) continue;
                await _loadoutManager.InstallItem(item, LoadoutId);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not install {Package} from Thunderstore", row.FullName);
            ErrorText = $"Could not install {row.FullName}: {e.Message}";
        }
    }

    private void ApplyFilter()
    {
        Packages = _catalog is null
            ? []
            : _catalog.Search(SearchText, ShowDeprecated).Select(ToRow).ToArray();
    }

    private static ThunderstoreVersionRef? LatestRef(NexusMods.Networking.Thunderstore.DTOs.PackageListing package, NexusMods.Networking.Thunderstore.DTOs.PackageVersion? latest)
    {
        if (latest is null || package.Owner is null) return null;
        if (!ThunderstorePackageRef.TryCreate(package.Owner, package.Name, out var reference)) return null;
        return ThunderstoreVersionNumber.TryParse(latest.VersionNumber, out var number) ? new ThunderstoreVersionRef(reference, number) : null;
    }

    private static ThunderstorePackageRow ToRow(NexusMods.Networking.Thunderstore.DTOs.PackageListing package)
    {
        var latest = package.Versions.FirstOrDefault(v => v.IsActive) ?? package.Versions.FirstOrDefault();
        return new ThunderstorePackageRow(
            package.FullName,
            latest?.VersionNumber ?? "",
            package.RatingScore,
            package.IsDeprecated,
            latest?.Description ?? "",
            Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var url) ? url : null,
            LatestRef(package, latest));
    }
}
