using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

public class NexusModsDesignViewModel : APageViewModel<INexusModsViewModel>, INexusModsViewModel
{
    public LoadoutId LoadoutId { get; set; }
    public string SearchText { get; set; } = "";
    public NexusModsSort Sort { get; set; } = NexusModsSort.Endorsements;

    public IReadOnlyList<NexusModsModRow> Mods { get; } =
    [
        new(new NexusModsSearchHit(ModId.From(107), "uid-107", "Example Overhaul", "Reworks the whole thing", "someone", "2.1", 48_000, 2_300_000,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null, null, false)),
    ];

    public IThumbnailLoader Thumbnails { get; } = new DesignThumbnailLoader();
    public INexusCollectionsViewModel Collections { get; } = new NexusCollectionsDesignViewModel();
    public bool HasMore => false;
    public bool IsLoading => false;
    public string ErrorText => "";
    public string InstallButtonText => "Install";
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<NexusModsModRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });
    public ReactiveCommand<NexusModsModRow, Unit> OpenCommand { get; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });

    public NexusModsDesignViewModel() : base(new DesignWindowManager()) { }
}
