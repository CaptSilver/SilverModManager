using System.Reactive;
using ReactiveUI;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

public class NexusCollectionsDesignViewModel : ReactiveObject, INexusCollectionsViewModel
{
    public string SearchText { get; set; } = "";
    public NexusCollectionsSort Sort { get; set; } = NexusCollectionsSort.Endorsements;

    public IReadOnlyList<NexusCollectionRow> Collections { get; } =
    [
        new(new NexusCollectionSearchHit(CollectionSlug.From("abc123"), GameDomain.From("cyberpunk2077"), "Example Collection", "A curated set of mods", "someone",
            4_800, 230_000, 120, 8_500_000_000UL, RevisionNumber.From(3), new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null, false)),
    ];

    public bool HasMore => false;
    public bool IsLoading => false;
    public string ErrorText => "";
    public bool IsVisible { get; set; }
    public IThumbnailLoader Thumbnails { get; } = new DesignThumbnailLoader();
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<NexusCollectionRow, Unit> PrimaryCommand { get; } = ReactiveCommand.Create<NexusCollectionRow>(_ => { });
    public ReactiveCommand<NexusCollectionRow, Unit> OpenOnNexusCommand { get; } = ReactiveCommand.Create<NexusCollectionRow>(_ => { });
}
