using SilverModManager.App.UI.Helpers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.Ini;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.Tools;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Observable = System.Reactive.Linq.Observable;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

public partial class LoadoutLeftMenuViewModel : AViewModel<ILoadoutLeftMenuViewModel>, ILoadoutLeftMenuViewModel
{
    public WorkspaceId WorkspaceId { get; }
    
    public ILeftMenuItemViewModel LeftMenuItemLibrary { get; }
    public ILeftMenuItemViewModel LeftMenuItemLoadout { get; }
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuStoreItems { get; }
    public ILeftMenuItemViewModel LeftMenuItemNewCollection { get; }
    public ILeftMenuItemViewModel LeftMenuItemHealthCheck { get; }
    public ILeftMenuItemViewModel LeftMenuItemTools { get; }
    public ILeftMenuItemViewModel? LeftMenuItemIni { get; }
    [Reactive] public partial ILeftMenuItemViewModel? LeftMenuItemExternalChanges { get; private set; }
    
    [Reactive] public partial bool HasSingleCollection { get; private set; } = true;
    public IApplyControlViewModel ApplyControlViewModel { get; }

    private readonly LoadoutCollectionList _collectionList;
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuCollectionItems => _collectionList.Items;
    
    [Reactive] private partial int NewDownloadModelCount { get; set; }

    public LoadoutLeftMenuViewModel(
        LoadoutContext loadoutContext,
        WorkspaceId workspaceId,
        IWorkspaceController workspaceController,
        IServiceProvider serviceProvider)
    {
        WorkspaceId = workspaceId;

        var diagnosticManager = serviceProvider.GetRequiredService<IDiagnosticManager>();
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        var conn = serviceProvider.GetRequiredService<IConnection>();
        var monitor = serviceProvider.GetRequiredService<IJobMonitor>();
        var overlayController = serviceProvider.GetRequiredService<IOverlayController>();
        var gameRunningTracker = serviceProvider.GetRequiredService<GameRunningTracker>();
        var logger = serviceProvider.GetRequiredService<ILogger<LoadoutLeftMenuViewModel>>();

        _collectionList = new LoadoutCollectionList(settingsManager, loadoutContext.LoadoutId);
        var collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();

        var loadout = Sdk.Loadouts.Loadout.Load(conn.Db, loadoutContext.LoadoutId.Value);
        var game = loadout.InstallationInstance.Game;

        // Library
        LeftMenuItemLibrary = new LeftMenuItemWithCountBadgeViewModel(
            workspaceController,
            WorkspaceId,
            new PageData
            {
                FactoryId = LibraryPageFactory.StaticId,
                Context = new LibraryPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                },
            }
        )
        {
            Text = new StringComponent(Language.LibraryPageTitle),
            Icon = IconValues.LibraryOutline,
            ToolTip = new StringComponent(Language.LibraryPageTitleToolTip),
            CountObservable = this.WhenAnyValue(vm => vm.NewDownloadModelCount),
        };
        
        // Loadout
        var loadoutLabelObservable = LoadoutDataProviderHelper
            .CountAllLoadoutItems(serviceProvider, loadoutContext.LoadoutId)
            .Select(count => string.Format(Language.LoadoutView_Title_Installed_Mods, count));

        LeftMenuItemLoadout = new LeftMenuItemViewModel(
            workspaceController,
            WorkspaceId,
            new PageData
            {
                FactoryId = LoadoutPageFactory.StaticId,
                Context = new LoadoutPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                    GroupScope = Optional<CollectionGroupId>.None,
                },
            }
        )
        {
            Text = new StringComponent(Language.LoadoutView_Title_Installed_Mods_Default, loadoutLabelObservable),
            Icon = IconValues.FormatAlignJustify,
            ToolTip = new StringComponent(Language.LoadoutView_Title_Installed_Mods_ToolTip),
        };

        LeftMenuStoreItems = new ReadOnlyObservableCollection<ILeftMenuItemViewModel>(
            new ObservableCollection<ILeftMenuItemViewModel>(StoreLeftMenuItems.For(game, loadoutContext.LoadoutId, workspaceController, WorkspaceId)));

        LeftMenuItemNewCollection = new NewCollectionViewModel(serviceProvider, loadout, workspaceController, workspaceId);

        var collectionRevisionsObservable = CollectionRevisionMetadata
            .ObserveAll(conn)
            .FilterImmutable(revision => game.NexusModsGameId.IsNexusGame(revision.Collection.GameId))
            .FilterOnObservable(revision =>
            {
                var groupObservable = collectionDownloader.GetCollectionGroupObservable(revision, loadout);
                var isNotInstalledObservable = collectionDownloader.IsCollectionInstalledObservable(revision, groupObservable).Select(static isInstalled => !isInstalled);
                return isNotInstalledObservable;
            })
            .Transform(ILeftMenuItemViewModel (revision) =>
            {
                var pageData = new PageData
                {
                    FactoryId = CollectionDownloadPageFactory.StaticId,
                    Context = new CollectionDownloadPageContext
                    {
                        TargetLoadout = loadout,
                        CollectionRevisionMetadataId = revision,
                    },
                };

                return new CollectionRevisionLeftMenuItemViewModel(
                    workspaceController, 
                    workspaceId, 
                    pageData,
                    revision,
                    serviceProvider)
                {
                    Text = new StringComponent(initialValue: revision.Collection.Name, CollectionMetadata.Observe(conn, revision.Collection).Select(x => x.Name)),
                    Icon = IconValues.CollectionsOutline,
                    RightIcon = IconValues.Downloading,
                };
            });

        // Collections
        var collectionItemsObservable = ObserveCollectionItems(conn, collectionDownloader, loadout, workspaceController, WorkspaceId, serviceProvider, _collectionList.MoveBy);
        
        // Health Check
        LeftMenuItemHealthCheck = new HealthCheckLeftMenuItemViewModel(
            workspaceController,
            settingsManager,
            WorkspaceId,
            new PageData
            {
                FactoryId = DiagnosticListPageFactory.StaticId,
                Context = new DiagnosticListPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                },
            },
            diagnosticManager,
            loadoutContext.LoadoutId
        )
        {
            Text = new StringComponent(Language.LoadoutLeftMenuViewModel_LoadoutLeftMenuViewModel_Diagnostics),
            Icon = IconValues.Cardiology,
            ToolTip = new StringComponent(Language.LoadoutLeftMenuViewModel_Diagnostics_ToolTip),
        };
        
        // Tools
        LeftMenuItemTools = new LeftMenuItemViewModel(
            workspaceController,
            WorkspaceId,
            new PageData
            {
                FactoryId = ToolsPageFactory.StaticId,
                Context = new ToolsPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                },
            }
        )
        {
            Text = new StringComponent("Tools"),
            Icon = IconValues.Wrench,
            ToolTip = new StringComponent("Run the tools for this game, or a Windows program in the game's prefix"),
        };

        // INI files, for a game whose INIs a loadout can own
        if (IniPageFactory.HasIniPage(game))
        {
            LeftMenuItemIni = new LeftMenuItemViewModel(
                workspaceController,
                WorkspaceId,
                new PageData
                {
                    FactoryId = IniPageFactory.StaticId,
                    Context = new IniPageContext
                    {
                        LoadoutId = loadoutContext.LoadoutId,
                    },
                }
            )
            {
                Text = new StringComponent(Language.IniPage_MenuItem),
                Icon = IconValues.FileEdit,
                ToolTip = new StringComponent(Language.IniPage_MenuItem_ToolTip),
            };
        }

        // Apply Control
        ApplyControlViewModel = new ApplyControlViewModel(loadoutContext.LoadoutId,
            serviceProvider,
            monitor,
            overlayController,
            gameRunningTracker
        );
        

        this.WhenActivated(disposable =>
            {
                // External Changes
                conn.ObserveDatoms(LoadoutOverridesGroup.OverridesFor, loadoutContext.LoadoutId)
                    .AsEntityIds()
                    .Transform(datom => LoadoutOverridesGroup.Load(conn.Db, datom.E))
                    .QueryWhenChanged(overrides =>
                    {
                        if (overrides.Count == 0)
                        {
                            return null;
                        }

                        Debug.Assert(overrides.Count == 1, "There should only be one LoadoutOverridesGroup for a LoadoutId");
                            
                        var group = overrides.Items.First().AsLoadoutItemGroup();
                             
                        return new LeftMenuItemViewModel(
                            workspaceController,
                            WorkspaceId,
                            new PageData
                            {
                                FactoryId = LoadoutGroupFilesPageFactory.StaticId,
                                Context = new LoadoutGroupFilesPageContext
                                {
                                    GroupIds = [ group.LoadoutItemGroupId ],
                                    IsReadOnly = false,
                                },
                            }
                        )
                        {
                            Text = new StringComponent(Language.LoadoutLeftMenuViewModel_External_Changes),
                            Icon = IconValues.FolderEditOutline,
                            ToolTip = new StringComponent(Language.LoadoutLeftMenuViewModel_External_Changes_ToolTip),
                        };

                    })
                    .OnUI()
                    .SubscribeWithErrorLogging(item => LeftMenuItemExternalChanges = item)
                    .DisposeWith(disposable);

                _collectionList.Bind(collectionItemsObservable.MergeChangeSets(collectionRevisionsObservable))
                    .DisposeWith(disposable);

                var previousCount = 0;
                NewDownloadModelCount = 0;

                LibraryDataProviderHelper
                    .CountAllLibraryItems(serviceProvider, loadoutContext.LoadoutId)
                    .OnUI()
                    .SubscribeWithErrorLogging(currentCount =>
                    {
                        if (LeftMenuItemLibrary.IsActive || LeftMenuItemLibrary.IsSelected)
                        {
                            NewDownloadModelCount = 0;
                        }
                        else
                        {
                            var diff = Math.Max(0, currentCount - previousCount);
                            NewDownloadModelCount += diff;
                        }

                        previousCount = currentCount;
                    })
                    .DisposeWith(disposable);

                LeftMenuItemLibrary
                    .WhenAnyValue(item => item.IsActive, item => item.IsSelected, (isActive, isSelected) => isActive || isSelected)
                    .Subscribe(isActive => NewDownloadModelCount = isActive ? 0 : NewDownloadModelCount)
                    .DisposeWith(disposable);

                CollectionGroup.ObserveAll(conn)
                    .Filter(collection => collection.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadoutContext.LoadoutId)
                    .Transform(collection => collection.Id)
                    .QueryWhenChanged(collections => collections.Count == 1)
                    .ToObservable()
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(hasSingleCollection => HasSingleCollection = hasSingleCollection)
                    .DisposeWith(disposable);
            }
        );
    }

    /// <inheritdoc/>
    public void MoveCollection(int fromIndex, int toIndex) => _collectionList.Move(fromIndex, toIndex);

    /// <inheritdoc/>
    public bool IsCollectionMovable(int index) => _collectionList.IsMovable(index);

    /// <summary>The loadout's collections as menu items: each opens its own page, or the loadout page scoped to it.</summary>
    internal static IObservable<IChangeSet<ILeftMenuItemViewModel, EntityId>> ObserveCollectionItems(
        IConnection conn,
        CollectionDownloader collectionDownloader,
        Sdk.Loadouts.Loadout.ReadOnly loadout,
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        IServiceProvider serviceProvider,
        Action<CollectionGroupId, int>? moveBy = null)
    {
        var defaultCollection = LoadoutCollectionOrder.DefaultCollectionOf(conn.Db, loadout.LoadoutId);
        return CollectionGroup.ObserveAll(conn)
            .FilterImmutable(f => f.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout.LoadoutId)
            .FilterOnObservable(group =>
                {
                    if (!group.TryGetAsNexusCollectionLoadoutGroup(out var nexusCollection))
                        return Observable.Return(true);
                    return collectionDownloader.IsCollectionInstalledObservable(
                        nexusCollection.Revision,
                        Observable.Return(Optional<CollectionGroup.ReadOnly>.Create(group))
                    );
                }
            )
            .SortBy(item => item.IsReadOnly)
            .Transform(collection =>
            {
                var pageData = collection.IsReadOnly
                    ? new PageData
                    {
                        FactoryId = CollectionLoadoutPageFactory.StaticId,
                        Context = new CollectionLoadoutPageContext
                        {
                            LoadoutId = loadout,
                            GroupId = collection,
                        },
                    }
                    : new PageData
                    {
                        FactoryId = LoadoutPageFactory.StaticId,
                        Context = new LoadoutPageContext
                        {
                            LoadoutId = loadout,
                            GroupScope = collection.CollectionGroupId,
                        },
                    };

                return new CollectionLeftMenuItemViewModel(
                    workspaceController,
                    workspaceId,
                    pageData,
                    serviceProvider,
                    collection.CollectionGroupId,
                    collection.CollectionGroupId == defaultCollection,
                    moveBy
                )
                {
                    Text = new StringComponent(initialValue: collection.AsLoadoutItemGroup().AsLoadoutItem().Name, LoadoutItem.Observe(conn, collection).Select(x => x.Name)),
                    Icon = IconValues.CollectionsOutline,
                    IsCollectionReadOnly = collection.IsReadOnly,
                };
            })
            .Transform(ILeftMenuItemViewModel (item) => item);
    }

}
