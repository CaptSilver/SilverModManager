using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using R3;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public class CollectionLoadoutDesignViewModel : APageViewModel<ICollectionLoadoutViewModel>, ICollectionLoadoutViewModel
{
    public CollectionLoadoutDesignViewModel() : base(new DesignWindowManager()) { }

    public LoadoutTreeDataGridAdapter Adapter { get; } = null!;
    public bool IsCollectionEnabled => true;
    public int InstalledModsCount { get; } = 10;
    public string Name => "Vanilla+ [Quality of Life]";
    
    public ulong EndorsementCount => 123456;
    public ulong TotalDownloads => 123456789;
    public Size TotalSize => Size.From(1234567890);
    public Percent OverallRating => Percent.CreateClamped(0.75);
    public RevisionNumber RevisionNumber { get; } = RevisionNumber.From(6);
    public string AuthorName => "Lowtonotolerance";
    public Bitmap? AuthorAvatar => new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/avatar.webp")));
    public Bitmap TileImage { get; } = new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/collection_tile_image.png")));
    public Bitmap BackgroundImage { get; } = new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/header-background.webp")));
    public ReactiveCommand<Unit> CommandToggle { get; } = new ReactiveCommand();
    public ReactiveCommand<Unit> CommandDeleteCollection { get; } = new ReactiveCommand();
    public ReactiveCommand<Unit> CommandMakeLocalEditableCopy { get; } = new ReactiveCommand();
    public ReactiveCommand<Unit> CommandReinstallCollection { get; } = new ReactiveCommand();

    public ReactiveCommand<Unit> CommandOpenOnNexusMods { get; } = new ReactiveCommand();
    public ReactiveCommand<Unit> CommandShowDownloads { get; } = new ReactiveCommand();
    public CollectionPageTab SelectedTab { get; set; } = CollectionPageTab.Installed;
    public ICollectionDownloadViewModel? Downloads { get; } = new CollectionDownloadDesignViewModel();
    public bool IsLocalCollection { get; } = false;
    public bool IsReadOnly { get; } = true;
}
