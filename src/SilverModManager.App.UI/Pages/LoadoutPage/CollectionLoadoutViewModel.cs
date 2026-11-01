using SilverModManager.Abstractions.Library;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using CompositeDisposable = System.Reactive.Disposables.CompositeDisposable;
using Disposable = System.Reactive.Disposables.Disposable;
using ReactiveCommand = R3.ReactiveCommand;
using SerialDisposable = System.Reactive.Disposables.SerialDisposable;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

/// <summary>
/// The page of one collection, whatever it came from: the header, the toolbar and the tabs all read a
/// <see cref="CollectionSource"/>. Everything the source exposes may emit off the UI thread, so every subscription
/// goes through <c>OnUI</c> before it touches bound state.
/// </summary>
public partial class CollectionLoadoutViewModel : APageViewModel<ICollectionLoadoutViewModel>, ICollectionLoadoutViewModel, ICollectionPageHost
{
    private readonly CollectionSource _source;
    private readonly IServiceProvider _services;
    private readonly IConnection _connection;
    private readonly IReadOnlyList<IContextMenuItem> _sourceMenuItems;

    // Only read and written on the UI thread.
    private Optional<CollectionGroupId> _group = Optional<CollectionGroupId>.None;

    public CollectionLoadoutViewModel(IWindowManager windowManager, IServiceProvider services, CollectionSource source) : base(windowManager)
    {
        _source = source;
        _services = services;
        _connection = services.GetRequiredService<IConnection>();

        TabIcon = IconValues.CollectionsOutline;
        TabTitle = source.InitialName;
        Name = source.InitialName;
        VersionText = source.VersionText;
        AuthorName = source.AuthorName;
        Stats = source.Stats;
        Badge = source.Badge;
        Link = source.Link;
        IsReadOnly = source.IsReadOnly;

        // The embedded page is built now and activated by its tab when it is first shown.
        Downloads = source.CreateDownloads();

        CommandToggle = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                if (_group.HasValue is false) return;
                var group = _group.Value;

                using var tx = _connection.BeginTransaction();
                if (IsCollectionEnabled)
                    tx.Add(group, LoadoutItem.Disabled, Null.Instance);
                else
                    tx.RetractCurrent(group, LoadoutItem.Disabled);
                await tx.Commit();
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        var viewDownloads = Downloads is null
            ? null
            : new ContextMenuItem
            {
                Header = Language.CollectionLoadoutView_ViewCollectionDownloadMenuItem,
                Icon = IconValues.Downloading,
                Command = ReactiveUI.ReactiveCommand.Create(() => { SelectedTab = CollectionPageTab.Downloads; }),
            };
        _sourceMenuItems = source.MenuItems(this, viewDownloads);
        MenuItems = _sourceMenuItems;

        // The first values decide the starting tab. Sources whose values are already known (Nexus, local) answer
        // while the page is constructed; the rest answer once, from wherever they emit.
        source.Group
            .CombineLatest(source.IsInstalled, static (group, isInstalled) => (Group: group, IsInstalled: isInstalled))
            .Take(1)
            .Subscribe(first => RunOnUi(() =>
            {
                var tabs = CollectionPageTabs.For(hasDownloadPage: Downloads is not null, hasInstalled: first.Group.HasValue);
                SelectedTab = CollectionPageTabs.Initial(tabs, first.IsInstalled && first.Group.HasValue);
            }));

        this.WhenActivated(disposables =>
        {
            var treeActivation = new SerialDisposable().AddTo(disposables);

            // The embedded page navigates and retitles through the tab it sits in.
            if (Downloads is { } downloads)
            {
                downloads.WindowId = WindowId;
                downloads.WorkspaceId = WorkspaceId;
                downloads.PanelId = PanelId;
                downloads.TabId = TabId;
            }

            source.Activate().AddTo(disposables);

            source.Group
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(group => ApplyGroup(group, treeActivation))
                .AddTo(disposables);

            source.Name.OnUI().Subscribe(name =>
            {
                Name = name;
                TabTitle = name;
            }).AddTo(disposables);
            source.StateLine.OnUI().Subscribe(line => StateLine = line).AddTo(disposables);
            source.Description.OnUI().Subscribe(description => Description = description).AddTo(disposables);
            source.CanToggle.OnUI().Subscribe(canToggle => IsToggleVisible = canToggle).AddTo(disposables);
            source.Instructions.OnUI().Subscribe(instructions => Instructions = instructions).AddTo(disposables);
            source.InstallProgress.OnUI().Subscribe(progress => InstallProgress = progress).AddTo(disposables);
            source.TileImage.OnUI().Subscribe(image => TileImage = image).AddTo(disposables);
            source.BackgroundImage.OnUI().Subscribe(image => BackgroundImage = image).AddTo(disposables);
            source.AuthorAvatar.OnUI().Subscribe(image => AuthorAvatar = image).AddTo(disposables);
        });
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // Builds the tree, the counters and the removal item for the group the source reports, or takes them away.
    private void ApplyGroup(Optional<CollectionGroupId> group, SerialDisposable treeActivation)
    {
        _group = group;
        treeActivation.Disposable = Disposable.Empty;
        HasInstalledTab = group.HasValue;

        if (group.HasValue is false)
        {
            Adapter = null;
            InstalledModsCount = 0;
            MenuItems = _sourceMenuItems;

            // No tab stays selected once it is hidden.
            if (SelectedTab == CollectionPageTab.Installed)
                SelectedTab = CollectionPageTabs.Initial(Tabs(hasInstalled: false), isInstalled: false);
            return;
        }

        var groupId = group.Value;
        var loadoutId = LoadoutItem.Load(_connection.Db, groupId).LoadoutId;
        var adapter = new LoadoutTreeDataGridAdapter(_services, new LoadoutFilter
        {
            LoadoutId = loadoutId,
            CollectionGroupId = LoadoutItemGroupId.From(groupId),
        });
        Adapter = adapter;
        MenuItems = [.._sourceMenuItems, RemovalItem(_source.InstalledRemoval(groupId))];

        treeActivation.Disposable = new CompositeDisposable(
            adapter.Activate(),
            _connection.ObserveDatoms(LoadoutItem.ParentId, groupId)
                .QueryWhenChanged(datoms => datoms.Count)
                .OnUI()
                .Subscribe(count => InstalledModsCount = count),
            LoadoutItem.Observe(_connection, groupId)
                .Select(static item => item.IsDisabled is false)
                .OnUI()
                .Subscribe(isEnabled => IsCollectionEnabled = isEnabled),
            HandleMessages(adapter, loadoutId)
        );
    }

    private IReadOnlyList<CollectionPageTab> Tabs(bool hasInstalled) =>
        CollectionPageTabs.For(hasDownloadPage: Downloads is not null, hasInstructions: Instructions.HasAny, hasInstalled: hasInstalled);

    // The page's own removal asks nothing; the left menu entry is the one that confirms.
    private ContextMenuItem RemovalItem(CollectionRemoval removal) => new()
    {
        Header = removal.Header,
        Icon = removal.Icon,
        Styling = removal.Styling,
        ToolTip = removal.ToolTip,
        Command = ReactiveUI.ReactiveCommand.CreateFromTask(
            () => removal.Remove(GetWorkspaceController()),
            removal.CanExecute.ObserveOn(RxApp.MainThreadScheduler)),
    };

    private IDisposable HandleMessages(LoadoutTreeDataGridAdapter adapter, LoadoutId loadoutId)
    {
        var windowManager = WindowManager;
        return adapter.MessageSubject.SubscribeAwait(async (message, cancellationToken) =>
        {
            await message.Match<Task>(
                toggleEnableStateMessage =>
                    LoadoutViewModel.HandleToggleItemEnabledState(toggleEnableStateMessage.Ids, _connection),
                openCollectionMessage =>
                {
                    LoadoutViewModel.HandleOpenItemCollectionPage(openCollectionMessage.Ids,
                        openCollectionMessage.NavigationInformation,
                        loadoutId, GetWorkspaceController(), _connection, _services.GetRequiredService<CollectionProviders>());
                    return Task.CompletedTask;
                },
                viewModPageMessage =>
                {
                    LoadoutViewModel.HandleOpenModPageFor(viewModPageMessage.Ids, _connection,
                        _services.GetRequiredService<IOSInterop>(),
                        _services.GetRequiredService<ModSourceResolver>(),
                        cancellationToken);
                    return Task.CompletedTask;
                },
                viewModFilesMessage =>
                {
                    LoadoutViewModel.HandleViewModFiles(viewModFilesMessage.Ids,
                        viewModFilesMessage.NavigationInformation,
                        _connection, GetWorkspaceController());
                    return Task.CompletedTask;
                },
                uninstallItemMessage => LoadoutViewModel.HandleUninstallItem(uninstallItemMessage.Ids, windowManager, _connection),
                toggleProtectedPathsMessage => LoadoutViewModel.HandleToggleProtectedPathsAllowed(toggleProtectedPathsMessage.Ids, _connection)
            );
        }, awaitOperation: AwaitOperation.Parallel, configureAwait: false);
    }

    IWindowManager ICollectionPageHost.WindowManager => WindowManager;
    IWorkspaceController ICollectionPageHost.WorkspaceController => GetWorkspaceController();

    public bool IsReadOnly { get; }
    public string AuthorName { get; }
    public string VersionText { get; }
    public CollectionStats Stats { get; }
    public CollectionBadge? Badge { get; }
    public CollectionLink? Link { get; }
    public IPageViewModelInterface? Downloads { get; }

    [Reactive] public partial LoadoutTreeDataGridAdapter? Adapter { get; private set; }
    [Reactive] public partial bool HasInstalledTab { get; private set; }
    [Reactive] public partial bool IsToggleVisible { get; private set; }
    [Reactive] public partial string Name { get; private set; }
    [Reactive] public partial string StateLine { get; private set; } = string.Empty;
    [Reactive] public partial string Description { get; private set; } = string.Empty;
    [Reactive] public partial IReadOnlyList<IContextMenuItem> MenuItems { get; private set; }
    [Reactive] public partial Bitmap? AuthorAvatar { get; private set; }
    [Reactive] public partial Bitmap? BackgroundImage { get; private set; }
    [Reactive] public partial Bitmap? TileImage { get; private set; }
    [Reactive] public partial bool IsCollectionEnabled { get; private set; }
    [Reactive] public partial int InstalledModsCount { get; private set; }
    [Reactive] public partial CollectionInstructions Instructions { get; private set; } = CollectionInstructions.None;
    [Reactive] public partial CollectionInstallStatus? InstallProgress { get; private set; }
    [Reactive] public partial CollectionPageTab SelectedTab { get; set; }

    public ReactiveCommand<Unit> CommandToggle { get; }
}
