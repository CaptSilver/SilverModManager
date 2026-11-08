using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using R3;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public class CollectionLoadoutDesignViewModel : APageViewModel<ICollectionLoadoutViewModel>, ICollectionLoadoutViewModel
{
    public CollectionLoadoutDesignViewModel() : base(new DesignWindowManager()) { }

    public LoadoutTreeDataGridAdapter? Adapter { get; } = new EmptyAdapter();
    
    /// <summary>
    /// A load order tree with no rows, so the page can be designed without a loadout in the database.
    /// </summary>
    private sealed class EmptyAdapter() : LoadoutTreeDataGridAdapter(DesignServices, new LoadoutFilter
    {
        LoadoutId = default,
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    })
    {
        private static readonly IServiceProvider DesignServices = new ServiceCollection()
            .AddSingleton<ISettingsManager, DesignSettingsManager>()
            .BuildServiceProvider();

        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical) =>
            System.Reactive.Linq.Observable.Empty<IChangeSet<CompositeItemModel<EntityId>, EntityId>>();
    }

    public bool HasInstalledTab { get; init; } = true;
    public bool IsToggleVisible { get; init; } = true;
    public bool IsCollectionEnabled => true;
    public int InstalledModsCount { get; } = 10;
    public string Name { get; init; } = "Vanilla+ [Quality of Life]";
    public ICollectionUpdate? Update { get; init; }
    public string StateLine { get; init; } = "Collection Installed";
    public string VersionText { get; init; } = "Revision 6";
    public string Description { get; init; } = "";
    public CollectionStats Stats { get; init; } = new(Percent.CreateClamped(0.75), 123456, 123456789, Size.From(1234567890), null);
    public string AuthorName => "Lowtonotolerance";
    public Bitmap? AuthorAvatar => new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/avatar.webp")));
    public Bitmap? TileImage { get; init; } = new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/collection_tile_image.png")));
    public Bitmap? BackgroundImage { get; } = new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/header-background.webp")));
    public CollectionBadge? Badge { get; init; } = new(IconValues.NexusColor, "Nexus Mods");
    public CollectionLink? Link { get; init; } = new("Open on Nexus Mods", IconValues.NexusColor, ReactiveUI.ReactiveCommand.Create(() => { }), System.Reactive.Linq.Observable.Return(true));
    public IReadOnlyList<IContextMenuItem> MenuItems { get; init; } =
    [
        new ContextMenuItem { Header = "Make editable copy", Icon = IconValues.Copy, Command = ReactiveUI.ReactiveCommand.Create(() => { }) },
        new ContextMenuItem { Header = "Uninstall collection", Icon = IconValues.PlaylistRemove, Command = ReactiveUI.ReactiveCommand.Create(() => { }) },
    ];
    public ReactiveCommand<Unit> CommandToggle { get; } = new ReactiveCommand();
    public Observable<Unit> ToggleRefused { get; } = Observable.Empty<Unit>();
    public CollectionPageTab SelectedTab { get; set; } = CollectionPageTab.Installed;
    public IPageViewModelInterface? Downloads { get; } = new CollectionDownloadDesignViewModel();
    public CollectionInstructions Instructions { get; } = new(
        new MarkdownRendererViewModel { Contents = "Install the script extender before anything else." },
        [new ModInstructions("Core mod", "Apply the patch after installing.", CollectionDownloader.ItemType.Required)],
        []);
    public bool IsReadOnly { get; init; } = true;
    public CollectionInstallStatus? InstallProgress { get; init; }
}
