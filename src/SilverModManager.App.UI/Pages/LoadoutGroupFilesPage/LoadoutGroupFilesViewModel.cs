using System.ComponentModel;
using System.Reactive.Disposables;
using Avalonia.Controls.Models.TreeDataGrid;
using DynamicData;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.TextEdit;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using ObservableCollections;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using CompositeDisposable = System.Reactive.Disposables.CompositeDisposable;
using SerialDisposable = System.Reactive.Disposables.SerialDisposable;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

[UsedImplicitly]
public partial class LoadoutGroupFilesViewModel : APageViewModel<ILoadoutGroupFilesViewModel>, ILoadoutGroupFilesViewModel
{
    // IViewLoadoutGroupFilesViewModel
    [Reactive] public partial LoadoutGroupFilesPageContext? Context { get; set; }
    [Reactive] public partial ReactiveCommand<NavigationInformation> OpenEditorCommand { get; [UsedImplicitly] set; } = null!;
    [Reactive] public partial ReactiveCommand<Unit> RemoveCommand { get; [UsedImplicitly] set; } = null!;
    public ReactiveCommand<Unit> UndoTreatAsMineCommand { get; }
    public ReactiveCommand<Unit> RefreshCommand { get; }
    [Reactive] public partial CompositeItemModel<GamePath>? SelectedItem { get; set; }
    [Reactive] public partial ViewLoadoutGroupFilesTreeDataGridAdapter? FileTreeAdapter { get; set; }
    
    public LoadoutGroupFilesViewModel(
        ILogger<LoadoutGroupFilesViewModel> logger,
        IWindowManager windowManager,
        IServiceProvider provider,
        IConnection connection) : base(windowManager)
    {
        TabIcon = IconValues.FolderOpen;
        TabTitle = "File Tree";

        // A file the user claimed from the game's own content can be handed back. Built here rather than on
        // activation: it only watches this view model, so it needs no disposal and the property is never unset.
        UndoTreatAsMineCommand = this.ObservePropertyChanged(vm => vm.SelectedItem)
            .Select((this, connection), static (item, state) =>
                item is not null && FindClaim(state.connection, state.Item1.Context, item.Key).HasValue)
            .ToReactiveCommand<Unit>(async (_, _) =>
                {
                    if (SelectedItem is null) return;
                    var claim = FindClaim(connection, Context, SelectedItem.Key);
                    if (!claim.HasValue) return;

                    var outcome = await provider.GetRequiredService<IGameInstalledContentService>().Unclaim(claim.Value);
                    if (!outcome.Restored)
                        provider.GetRequiredService<IWindowNotificationService>()
                            .ShowToast(string.Format(Language.GameInstalled_UndoTreatAsMine_Failed, outcome.Reason));
                }
            );

        // Only the loadout's External Changes can have gone stale: it mirrors the game folder, which cleanups and
        // other tools change without a sync.
        RefreshCommand = this.ObservePropertyChanged(vm => vm.Context)
            .Select(connection, static (context, conn) => FindExternalChangesLoadout(conn, context).HasValue)
            .ToReactiveCommand<Unit>(async (_, _) => await RescanGameFolder(logger, provider, connection));

        this.WhenActivated(disposables =>
        {
            // The folder may have changed since External Changes was last built. The reindex only writes when it
            // finds a difference, so opening the page costs a stat of the folder when nothing moved.
            this.ObservePropertyChanged(vm => vm.Context)
                .WhereNotNull()
                .SubscribeAwait(async (_, _) => await RescanGameFolder(logger, provider, connection), AwaitOperation.Drop)
                .DisposeWith(disposables);

            // Note(sewer):
            // For instant disposing of the TreeDataGridAdapter(s), for when the Context of the
            // ViewModel is dynamically changed. Since the adapter may hold a lot of memory (a whole file tree that beams events),
            // immediate disposal is preferable for saving CPU cycles and memory.
            SerialDisposable treeDataGridAdapterDisposable = new();
            
            OpenEditorCommand = this.ObservePropertyChanged(vm => vm.SelectedItem)
                .Select((this, connection), static (item, state) =>
                {
                    // If no item is currently selected, we obviously can't open the editor.
                    if (item == null)
                        return false;

                    var gamePath = item.Key;
                    var itemWithPath = LoadoutItemGroupHelpers.FindMatchingFile
                        (state.connection, state.Item1.Context!.GroupIds, gamePath, false);
                    
                    // Don't open the editor if no file is found (it's a directory),
                    // or if it is not a LoadoutFile (it's a DeletedFile).
                    return itemWithPath.HasValue && itemWithPath.Value.IsLoadoutFile();
                }
            )
            .ToReactiveCommand<NavigationInformation>(info =>
            {
                // Note(sewer): Is it possible to avoid a double entity load here?
                var gamePath = SelectedItem!.Key;
                var itemWithPath = LoadoutItemGroupHelpers.FindMatchingFile(connection, Context!.GroupIds, gamePath);

                if (!itemWithPath.HasValue || !itemWithPath.Value.TryGetAsLoadoutFile(out var loadoutItem)) 
                    return;

                var pageData = new PageData
                {
                    FactoryId = TextEditorPageFactory.StaticId,
                    Context = new TextEditorPageContext
                    {
                        FileId = loadoutItem.LoadoutFileId, // not null because command was executable
                        FilePath = gamePath.Path,
                        IsReadOnly = Context!.IsReadOnly,
                    },
                };

                var workspaceController = windowManager.ActiveWorkspaceController;

                var behavior = workspaceController.GetOpenPageBehavior(pageData, info);
                var workspaceId = workspaceController.ActiveWorkspaceId;
                workspaceController.OpenPage(workspaceId, pageData, behavior);
            })
            .DisposeWith(disposables);

            var writeableContextObservable = this.ObservePropertyChanged(vm => vm.Context).Select(context => context is { IsReadOnly: false });
            var hasSelectionObservable = this.ObservePropertyChanged(vm => vm.SelectedItem).Select(item => item is not null);
            
            // ReSharper disable once InvokeAsExtensionMethod
            RemoveCommand = Observable.CombineLatest(
                    writeableContextObservable, hasSelectionObservable,
                    resultSelector: (isWritable, hasSelection) => isWritable && hasSelection
                )
                .ToReactiveCommand<Unit>(async (_, _) =>
                    {
                        var gamePath = SelectedItem!.Key;
                        
                        // Unselect last item.
                        SelectedItem = null;
                        
                        var result = await LoadoutItemGroupHelpers.RemoveFileOrFolder(connection, Context!.GroupIds, gamePath, requireAllGroups: false);
                        if (result == LoadoutItemGroupHelpers.GroupOperationStatus.NoItemsDeleted)
                            logger.LogError("Unable to find Loadout files with path `{Path}` in groups: {Groups}", 
                                gamePath, string.Join(", ", Context!.GroupIds));

                    }
                )
                .DisposeWith(disposables);
            
            // Set the title based on the first valid group
            this.ObservePropertyChanged(vm => vm.Context)
                .WhereNotNull()
                // ReSharper disable once HeapView.CanAvoidClosure
                .Select(context => LoadoutItemGroupHelpers.GetFirstValidGroup(connection, context.GroupIds))
                .Where(group => group != null)
                .Do(group => TabTitle = group!.Value.AsLoadoutItem().Name)
                .Subscribe()
                .DisposeWith(disposables);

            // Populate the file tree
            this.ObservePropertyChanged(vm => vm.Context)
                .WhereNotNull()
                .Do(context =>
                    {
                        // Dispose any existing adapter first, in case context is changed.
                        FileTreeAdapter?.Dispose();
                        FileTreeAdapter = new ViewLoadoutGroupFilesTreeDataGridAdapter(provider, new ModFilesFilter(context.GroupIds.ToArray()));

                        var compositeDisposable = new CompositeDisposable();
                        FileTreeAdapter.Activate().AddTo(compositeDisposable);
                        FileTreeAdapter.SelectedModels
                            .ObserveChanged()
                            .Subscribe(FileTreeAdapter, (_, adapter) =>
                                {
                                    SelectedItem = adapter.SelectedModels.FirstOrDefault();
                                }
                            )
                            .AddTo(compositeDisposable);
                        FileTreeAdapter.AddTo(compositeDisposable); // cleanup self.
                        
                        // Update the selection subscription
                        // Note: This auto disposes last.
                        treeDataGridAdapterDisposable.Disposable = compositeDisposable;
            
                        // Initial update
                        SelectedItem = FileTreeAdapter.SelectedModels.FirstOrDefault();
                    }
                )
                .Subscribe()
                .DisposeWith(disposables);

            treeDataGridAdapterDisposable.DisposeWith(disposables);
        });
    }
    
    /// <summary>The loadout whose External Changes the page shows, or None for any other group.</summary>
    private static Optional<LoadoutId> FindExternalChangesLoadout(IConnection connection, LoadoutGroupFilesPageContext? context)
    {
        if (context is null) return Optional<LoadoutId>.None;
        var group = LoadoutItemGroupHelpers.GetFirstValidGroup(connection, context.GroupIds);
        if (group is null || !LoadoutOverridesGroup.Load(connection.Db, group.Value.Id).IsValid()) return Optional<LoadoutId>.None;
        return group.Value.AsLoadoutItem().LoadoutId;
    }

    /// <summary>Reads the game folder again for the shown External Changes; says so in a toast when it cannot.</summary>
    private async Task RescanGameFolder(ILogger logger, IServiceProvider provider, IConnection connection)
    {
        var loadoutId = FindExternalChangesLoadout(connection, Context);
        if (!loadoutId.HasValue) return;

        try
        {
            var loadout = Loadout.Load(connection.Db, loadoutId.Value);
            var loadouts = provider.GetRequiredService<ILoadoutManager>();
            await Task.Run(() => loadouts.RescanGameFolder(loadout));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Reading the game folder again for External Changes failed");
            provider.GetRequiredService<IWindowNotificationService>()
                .ShowToast($"The app could not read the game folder: {e.Message}");
        }
    }

    private static Optional<GameInstalledClaimId> FindClaim(IConnection connection, LoadoutGroupFilesPageContext? context, GamePath path)
    {
        if (context is null) return Optional<GameInstalledClaimId>.None;
        var group = LoadoutItemGroupHelpers.GetFirstValidGroup(connection, context.GroupIds);
        return group is null
            ? Optional<GameInstalledClaimId>.None
            : GameInstalledClaimLookup.Find(connection.Db, group.Value.AsLoadoutItem().LoadoutId, path);
    }

    /// <summary>
    /// Returns the appropriate LoadoutItemGroup of files if the selection contains a LoadoutItemGroup containing files,
    /// if the selection contains multiple LoadoutItemGroups of files, returns None.
    /// If the group is completely empty, then it is assumed to be an empty mod and it is returned.
    /// </summary>
    internal static Optional<LoadoutItemGroup.ReadOnly> GetViewModFilesLoadoutItemGroup(
        IReadOnlyCollection<LoadoutItemId> loadoutItemIds, 
        IConnection connection)
    {
        var db = connection.Db;
        // Only allow when selecting a single item, or an item with a single child
        if (loadoutItemIds.Count != 1) return Optional<LoadoutItemGroup.ReadOnly>.None;
        var currentGroupId = loadoutItemIds.First();
        
        var groupDatoms = db.Datoms(LoadoutItemGroup.Group, Null.Instance);

        while (true)
        {
            var childDatoms = db.Datoms(LoadoutItem.ParentId, currentGroupId);
            
            // If no children, assume it's an empty mod and return the group
            if (childDatoms.Count == 0) return LoadoutItemGroup.Load(db, currentGroupId);

            var childGroups = groupDatoms.MergeByEntityId(childDatoms);

            // We have no child groups, check if children are files
            if (childGroups.Count == 0)
            {
                return LoadoutItemWithTargetPath.TryGet(db, childDatoms[0].E, out _) 
                    ? LoadoutItemGroup.Load(db, currentGroupId)
                    : Optional<LoadoutItemGroup.ReadOnly>.None;
            }
            
            // Single child group, check if that group is valid
            if (childGroups.Count == 1)
            {
                currentGroupId = childGroups.First();
                continue;
            }
        
            // We have multiple child groups, return None
            if (childGroups.Count > 1) return Optional<LoadoutItemGroup.ReadOnly>.None;
        }
    }
}

/// <summary>
/// Implementation of <see cref="TreeDataGridAdapter{TModel,TKey}"/> 
/// </summary>
public class ViewLoadoutGroupFilesTreeDataGridAdapter(IServiceProvider serviceProvider, ModFilesFilter filesFilter) : TreeDataGridAdapter<CompositeItemModel<GamePath>, GamePath>(serviceProvider)
{
    private readonly LoadoutGroupFilesProvider _loadoutGroupFilesProvider = new(serviceProvider);
    
    protected override IObservable<IChangeSet<CompositeItemModel<GamePath>, GamePath>> GetRootsObservable(bool viewHierarchical) => _loadoutGroupFilesProvider.ObserveModFiles(filesFilter, useFullFilePaths: !viewHierarchical);

    protected override IColumn<CompositeItemModel<GamePath>>[] CreateColumns(bool viewHierarchical)
    {
        var nameColumn = ColumnCreator.Create<GamePath, SharedColumns.NameWithFileIcon>();

        return
        [
            viewHierarchical ? ITreeDataGridItemModel<CompositeItemModel<GamePath>, GamePath>.CreateExpanderColumn(nameColumn) : nameColumn,
            ColumnCreator.Create<GamePath, SharedColumns.ItemSizeOverGamePath>(sortDirection: ListSortDirection.Descending),
            ColumnCreator.Create<GamePath, SharedColumns.FileCount>(),
        ];
    }
}
