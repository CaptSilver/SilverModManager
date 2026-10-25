using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

public class WabbajackGalleryDesignViewModel : APageViewModel<IWabbajackGalleryViewModel>, IWabbajackGalleryViewModel
{
    public LoadoutId LoadoutId { get; set; }
    public string SearchText { get; set; } = "";
    public WabbajackGallerySort Sort { get; set; }

    public IReadOnlyList<WabbajackGalleryRow> Lists { get; } =
    [
        new(new GalleryEntry
        {
            Title = "Nightfall",
            Author = "Mara",
            Version = "4.2.1",
            Description = "A survival overhaul with a rebuilt economy, new quests and a lot of weather.",
            Tags = ["Quests", "Survival", "Gameplay"],
            GalleryGame = "skyrimspecialedition",
            MachineUrl = "nightfall",
            Repository = "wabbajack-tools/mod-lists",
            IsOfficial = true,
            ArchivesSize = 85UL * 1024 * 1024 * 1024,
            InstalledSize = 210UL * 1024 * 1024 * 1024,
            Readme = new Uri("https://example.invalid/readme"),
        }),
        new(new GalleryEntry
        {
            Title = "Daybreak",
            Author = "Jonas",
            Version = "1.0",
            GalleryGame = "skyrimspecialedition",
            MachineUrl = "daybreak",
            Repository = "someone/community-lists",
        }),
    ];

    public IThumbnailLoader Thumbnails { get; } = new DesignThumbnailLoader();
    public string ErrorText => "";
    public string RefreshedText => "Last refreshed 1 Oct 12:00";
    public bool IsRefreshing => false;
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<WabbajackGalleryRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<WabbajackGalleryRow>(_ => { });
    public ReactiveCommand<Uri, Unit> OpenLinkCommand { get; } = ReactiveCommand.Create<Uri>(_ => { });

    public WabbajackGalleryDesignViewModel() : base(new DesignWindowManager()) { }
}
