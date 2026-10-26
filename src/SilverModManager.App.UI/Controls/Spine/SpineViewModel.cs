using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Controls.Spine.Buttons;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.Pages.Downloads;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Spine;

[UsedImplicitly]
public partial class SpineViewModel : AViewModel<ISpineViewModel>, ISpineViewModel
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SpineViewModel> _logger;
    private readonly IWindowManager _windowManager;
    private readonly ISynchronizerService _syncService;

    // The view model owns the bound list so a new saved order can move icons into place; swapping the sort comparer
    // instead makes DynamicData clear and refill the list, and the view rebuilds every tile (the spine blinks).
    private readonly ObservableCollectionExtended<IImageButtonViewModel> _loadoutSpineList = new();
    private readonly LoadoutSpineEntriesComparer _loadoutOrder = new();

    // The visible loadouts as of the last change, read and written on the UI thread only.
    private Loadout.ReadOnly[]? _visibleLoadouts;
    private readonly ReadOnlyObservableCollection<IImageButtonViewModel> _loadoutSpineItems;
    private readonly ISettingsManager _settingsManager;
    public ReadOnlyObservableCollection<IImageButtonViewModel> LoadoutSpineItems => _loadoutSpineItems;
    public IIconButtonViewModel Home { get; }
    public IIconButtonViewModel AddLoadout { get; }
    public ISpineDownloadButtonViewModel Downloads { get; }
    private IList<ISpineItemViewModel> _specialSpineItems = new List<ISpineItemViewModel>();

    private ISpineItemViewModel? _activeSpineItem;

    private Dictionary<WorkspaceId, ILeftMenuViewModel> _leftMenus = new([]);
    private readonly IConnection _conn;
    [Reactive] public partial ILeftMenuViewModel? LeftMenuViewModel { get; private set; }

    public SpineViewModel(
        IServiceProvider serviceProvider,
        ILogger<SpineViewModel> logger,
        IConnection conn,
        IWindowManager windowManager,
        IIconButtonViewModel addButtonViewModel,
        IIconButtonViewModel homeButtonViewModel,
        ISpineDownloadButtonViewModel spineDownloadsButtonViewModel,
        IWorkspaceAttachmentsFactoryManager workspaceAttachmentsFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _loadoutSpineItems = new ReadOnlyObservableCollection<IImageButtonViewModel>(_loadoutSpineList);
        _windowManager = windowManager;
        _conn = conn;
        _syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();

        // Setup the special spine items
        Home = homeButtonViewModel;
        Home.Name = Language.SpineHomeButton_ToolTip_Home;
        Home.WorkspaceContext = new HomeContext();
        _specialSpineItems.Add(Home);
        Home.Click = ReactiveCommand.Create(NavigateToHome);

        AddLoadout = addButtonViewModel;
        AddLoadout.Name = "Add";
        AddLoadout.WorkspaceContext = new HomeContext();
        _specialSpineItems.Add(AddLoadout);
        AddLoadout.Click = ReactiveCommand.Create(NavigateToMyGames);

        Downloads = spineDownloadsButtonViewModel;
        Downloads.WorkspaceContext = new DownloadsContext();
        _specialSpineItems.Add(Downloads);
        Downloads.Click = ReactiveCommand.Create(NavigateToDownloads);

        var workspaceController = windowManager.ActiveWorkspaceController;

        this.WhenActivated(disposables =>
            {
                var loadouts = Loadout.ObserveAll(_conn);
                var gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();

                R3.ObservableExtensions
                    .AsSystemObservable(_settingsManager.GetChanges<LoadoutSpineOrderSettings>(prependCurrent: true))
                    // Re-sorting the bound collection is something only the UI thread may do.
                    .OnUI()
                    .Subscribe(settings =>
                    {
                        _loadoutOrder.SetSavedOrder(settings.LoadoutIds);
                        MoveIconsIntoOrder();
                    })
                    .DisposeWith(disposables);

                loadouts
                    // A loadout whose game can't be found would make the icon transform throw and end
                    // the whole stream, taking every other icon off the spine with it.
                    .Filter(loadout => loadout.IsVisible() && gameRegistry.TryGetGameInstallation(loadout, out _))
                    .TransformAsync(async loadout =>
                        {
                            await using var iconStream = await loadout.InstallationInstance.Game.IconImage.GetStreamAsync();

                            var vm = serviceProvider.GetRequiredService<IImageButtonViewModel>();
                            vm.Name = LoadoutLabel.For(loadout, loadout.Db);
                            vm.Image = LoadImageFromStream(iconStream);
                            vm.LoadoutBadgeViewModel = new LoadoutBadgeViewModel(_conn, _syncService, hideOnSingleLoadout: true);
                            vm.LoadoutBadgeViewModel.LoadoutValue = loadout;
                            vm.WorkspaceContext = new LoadoutContext { LoadoutId = loadout.LoadoutId };
                            vm.Click = ReactiveCommand.Create(() => ChangeToLoadoutWorkspace(loadout.LoadoutId));
                            vm.ChangeLoadout = ReactiveCommand.Create(() => NavigateToLoadoutsOf(loadout.InstallationId));
                            vm.MoveUp = ReactiveCommand.Create(() => MoveLoadoutBy(loadout.LoadoutId, -1));
                            vm.MoveDown = ReactiveCommand.Create(() => MoveLoadoutBy(loadout.LoadoutId, 1));
                            vm.IsActive = false;

                            if (workspaceController.ActiveWorkspace.Context is LoadoutContext activeLoadoutContext &&
                                activeLoadoutContext.LoadoutId == loadout.LoadoutId)
                            {
                                SetActiveItem(vm);
                            }
                            
                            return vm;
                        }
                    )
                    .OnUI()
                    // The icon load above is awaited, so a sibling can come or go before this button is bound and
                    // the relabel below would miss it; name it against the latest visible set as it arrives.
                    .OnItemAdded(loadoutSpineItem => Relabel(loadoutSpineItem, _visibleLoadouts))
                    .OnItemRemoved(loadoutSpineItem =>
                        {
                            if (loadoutSpineItem.WorkspaceContext is LoadoutContext loadoutContext)
                                workspaceController.UnregisterWorkspaceByContext<LoadoutContext>(context => loadoutContext == context);
                        })
                // A changed order must move icons, not clear and refill the list: a reset makes the view rebuild every tile.
                .SortAndBind(_loadoutSpineList, _loadoutOrder)
                .SubscribeWithErrorLogging()
                .DisposeWith(disposables);

            // A button's label depends on how many loadouts its game has, so adding or deleting one relabels the others.
            loadouts
                .Filter(loadout => loadout.IsVisible())
                .QueryWhenChanged(query => query.Items.ToArray())
                .OnUI()
                .SubscribeWithErrorLogging(visible =>
                {
                    _visibleLoadouts = visible;
                    foreach (var item in _loadoutSpineList)
                        Relabel(item, visible);
                })
                .DisposeWith(disposables);

            // Create Left Menus for each workspace on demand
            workspaceController.AllWorkspaces
                .ToObservableChangeSet()
                .OnItemAdded(workspace =>
                {
                    if (_leftMenus.TryGetValue(workspace.Id, out _))
                    {
                        return;
                    }
                        
                    try
                    {
                        var leftMenu = workspaceAttachmentsFactory.CreateLeftMenuFor(
                            workspace.Context,
                            workspace.Id,
                            workspaceController
                        );

                        if (leftMenu == null)
                        {
                            throw new InvalidDataException("LeftMenu factory returned a null view model");
                        }
                        
                        _leftMenus.Add(workspace.Id, leftMenu);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Exception while creating left menu for context {Context}", workspace.Context);
                    }
                })
                .OnItemRemoved(workspace => _leftMenus.Remove(workspace.Id, out _))
                .SubscribeWithErrorLogging()
                .DisposeWith(disposables);

            // Navigate away from the Loadout workspace if the Loadout is removed
            loadouts
                .OnUI()
                .OnItemRemoved(loadout =>
                    {
                        if (workspaceController.ActiveWorkspace.Context is LoadoutContext activeLoadoutContext &&
                            activeLoadoutContext.LoadoutId == loadout.LoadoutId)
                        {
                            workspaceController.ChangeOrCreateWorkspaceByContext<HomeContext>(() => new PageData
                                {
                                    FactoryId = MyGamesPageFactory.StaticId,
                                    Context = new MyGamesPageContext(),
                                }
                            );
                        }
                    }, false
                )
                .SubscribeWithErrorLogging()
                .DisposeWith(disposables);

            // Update the LeftMenuViewModel when the active workspace changes
            workspaceController.WhenAnyValue(controller => controller.ActiveWorkspace)
                .Select(workspace => workspace.Id)
                .Select(workspaceId => _leftMenus.GetValueOrDefault(workspaceId))
                .BindToVM(this, vm => vm.LeftMenuViewModel)
                .DisposeWith(disposables);

            // Update the active spine item when the active workspace changes
            workspaceController
                .WhenAnyValue(controller => controller.ActiveWorkspace)
                .Select(workspace => workspace.Context)
                .WhereNotNull()
                .SubscribeWithErrorLogging(context =>
                    {
                        var itemToActivate = _specialSpineItems
                            .Concat(_loadoutSpineItems)
                            .FirstOrDefault(spineItem => spineItem.WorkspaceContext?.Equals(context) == true);

                        SetActiveItem(itemToActivate);
                    }
                )
                .DisposeWith(disposables);
            
            }
        );
    }

    private void SetActiveItem(ISpineItemViewModel? itemToActivate)
    {
        if (itemToActivate == null)
            return;

        if (_activeSpineItem != null)
            _activeSpineItem.IsActive = false;

        itemToActivate.IsActive = true;
        _activeSpineItem = itemToActivate;
    }

    private Bitmap LoadImageFromStream(Stream iconStream)
    {
        try
        {
            return Bitmap.DecodeToWidth(iconStream, (int) ImageSizes.GameThumbnail.Width);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skia image load error, while loading image from stream");
            // Null images are fine, they will be ignored
            return null!;
        }
    }

    public void NavigateToHome()
    {
        var workspaceController = _windowManager.ActiveWorkspaceController;

        workspaceController.ChangeOrCreateWorkspaceByContext<HomeContext>(() => new PageData
            {
                FactoryId = MyGamesPageFactory.StaticId,
                Context = new MyGamesPageContext()
            }
        );
    }

    private void ChangeToLoadoutWorkspace(LoadoutId loadoutId)
    {
        var workspaceController = _windowManager.ActiveWorkspaceController;

        workspaceController.ChangeOrCreateWorkspaceByContext(
            context => context.LoadoutId == loadoutId,
            () => new PageData
            {
                FactoryId = LoadoutPageFactory.StaticId,
                Context = new LoadoutPageContext
                {
                    LoadoutId = loadoutId,
                    GroupScope = Optional<CollectionGroupId>.None,
                }
            },
            () => new LoadoutContext
            {
                LoadoutId = loadoutId
            }
        );
    }

    private void NavigateToMyGames()
    {
        var workspaceController = _windowManager.ActiveWorkspaceController;

        var pageData = new PageData
        {
            FactoryId = MyGamesPageFactory.StaticId,
            Context = new MyGamesPageContext(),
        };

        var ws = workspaceController.ChangeOrCreateWorkspaceByContext<HomeContext>(() => pageData);
        var behavior = workspaceController.GetOpenPageBehavior(pageData, NavigationInformation.From(NavigationInput.Default));
        workspaceController.OpenPage(ws.Id, pageData, behavior);
    }

    /// <summary>
    /// Opens My Loadouts for one game in the Home workspace, the same place the Home menu opens it.
    /// </summary>
    private void NavigateToLoadoutsOf(GameInstallMetadataId installationId)
    {
        var workspaceController = _windowManager.ActiveWorkspaceController;

        var pageData = new PageData
        {
            FactoryId = MyLoadoutsPageFactory.StaticId,
            Context = new MyLoadoutsPageContext { InstallationId = installationId },
        };

        var ws = workspaceController.ChangeOrCreateWorkspaceByContext<HomeContext>(() => pageData);
        var behavior = workspaceController.GetOpenPageBehavior(pageData, NavigationInformation.From(NavigationInput.Default));
        workspaceController.OpenPage(ws.Id, pageData, behavior);
    }

    private void NavigateToDownloads()
    {
        var workspaceController = _windowManager.ActiveWorkspaceController;

        workspaceController.ChangeOrCreateWorkspaceByContext<DownloadsContext>(() => new PageData
            {
                FactoryId = DownloadsPageFactory.StaticId,
                Context = new DownloadsPageContext { GameScope = Optional<NexusModsGameId>.None }
            }
        );
    }

    /// <inheritdoc/>
    public void MoveLoadout(int fromIndex, int toIndex)
    {
        var displayed = _loadoutSpineItems
            .Select(LoadoutIdOf)
            .ToList();

        // Saving the displayed order is also what drops ids of deleted loadouts from the setting.
        var moved = LoadoutSpineOrder.Move(displayed, fromIndex, toIndex);
        _logger.LogTrace("Moving spine loadout from {From} to {To}: {Before} -> {After}", fromIndex, toIndex,
            string.Join(",", displayed.Select(id => id.Value.Value)), string.Join(",", moved.Select(id => id.Value.Value)));
        _settingsManager.Update<LoadoutSpineOrderSettings>(settings => settings with
        {
            LoadoutIds = moved.Select(id => id.Value.Value).ToImmutableList(),
        });
    }

    private void MoveLoadoutBy(LoadoutId loadoutId, int offset)
    {
        var index = _loadoutSpineItems.ToList().FindIndex(item => LoadoutIdOf(item) == loadoutId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _loadoutSpineItems.Count) return;
        MoveLoadout(index, target);
    }

    // Every loadout icon gets its badge in the transform above, so a missing one is a bug, not an empty slot.
    private static LoadoutId LoadoutIdOf(IImageButtonViewModel item)
    {
        var loadout = item.LoadoutBadgeViewModel?.LoadoutValue
            ?? throw new InvalidOperationException($"Spine icon '{item.Name}' has no loadout badge");
        if (!loadout.HasValue) throw new InvalidOperationException($"Spine icon '{item.Name}' has no loadout");
        return loadout.Value.LoadoutId;
    }

    /// <summary>
    /// Loadouts in the saved order come first; the rest follow by installation, then loadout creation time.
    /// </summary>
    /// <summary>Moves icons whose place changed under a new saved order, one Move each, so no tile is rebuilt.</summary>
    private static void Relabel(IImageButtonViewModel item, Loadout.ReadOnly[]? visibleLoadouts)
    {
        if (visibleLoadouts is not null && item.LoadoutBadgeViewModel?.LoadoutValue is { HasValue: true } loadout)
            item.Name = LoadoutLabel.For(loadout.Value, visibleLoadouts);
    }

    private void MoveIconsIntoOrder()
    {
        var sorted = _loadoutSpineList.OrderBy(item => item, _loadoutOrder).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(_loadoutSpineList[i], sorted[i])) continue;
            _loadoutSpineList.Move(_loadoutSpineList.IndexOf(sorted[i]), i);
        }
    }

    /// <summary>
    /// Orders the spine's loadouts. One instance lives as long as the view model; a new saved order updates its
    /// positions in place (<see cref="SetSavedOrder"/>) rather than replacing it.
    /// </summary>
    private class LoadoutSpineEntriesComparer : IComparer<IImageButtonViewModel>
    {
        private Dictionary<ulong, int> _positions = new();

        public void SetSavedOrder(IReadOnlyList<ulong> savedOrder) => _positions = savedOrder
            .Select((id, index) => (id, index))
            .DistinctBy(pair => pair.id)
            .ToDictionary(pair => pair.id, pair => pair.index);

        public int Compare(IImageButtonViewModel? x, IImageButtonViewModel? y)
        {
            var xloadout = x?.LoadoutBadgeViewModel?.LoadoutValue;
            var yloadout = y?.LoadoutBadgeViewModel?.LoadoutValue;

            if (xloadout == null) return yloadout == null ? 0 : -1;
            if (yloadout == null) return 1;

            var xPosition = _positions.GetValueOrDefault(xloadout.Value.Value.LoadoutId.Value.Value, int.MaxValue);
            var yPosition = _positions.GetValueOrDefault(yloadout.Value.Value.LoadoutId.Value.Value, int.MaxValue);
            if (xPosition != yPosition) return xPosition.CompareTo(yPosition);

            if (xloadout.Value.Value.Installation.Path != yloadout.Value.Value.Installation.Path)
                return DateTimeOffset.Compare(xloadout.Value.Value.Installation.GetCreatedAt(), yloadout.Value.Value.Installation.GetCreatedAt());

            return DateTimeOffset.Compare(xloadout.Value.Value.GetCreatedAt(), yloadout.Value.Value.GetCreatedAt());
        }
    }
}
