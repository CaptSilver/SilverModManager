using System.Reactive;
using ReactiveUI;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Catalog;

public class CatalogDesignViewModel : APageViewModel<ICatalogViewModel>, ICatalogViewModel
{
    private sealed record DesignHandle : ICatalogHandle
    {
        public string Catalog => "Thunderstore";
    }

    private static CatalogCardRow Row(string title, string author, string summary, string version, long downloads, double? rating, Uri? page)
        => new(new CatalogCard(author + "-" + title, title, author, summary, null, page, version, downloads, rating, false, null, new DesignHandle()), "Install");

    public void Initialize(string catalog, LoadoutId loadoutId) { }
    public string CatalogName => "Thunderstore";
    public string SearchText { get; set; } = "";
    public string SortKey { get; set; } = "TopRated";
    public IReadOnlyList<StoreSortOption> SortOptions { get; } = [new("TopRated", "Top rated"), new("Downloads", "Most downloaded")];

    public IReadOnlyList<CatalogCardRow> Rows { get; } =
    [
        Row("BepInExPack", "BepInEx", "Unity mod loader", "5.4.2305", 4_200_000, 1500, new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/")),
        Row("REPOLib", "Zehs", "Library for R.E.P.O. mod developers", "4.2.0", 830_000, 900, new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/")),
        Row("Abandoned", "Old", "No longer maintained", "0.3.1", 900, null, null),
    ];

    public bool HasMore => false;
    public bool IsLoading => false;
    public string ErrorText => "";
    public string StatusText => "";
    public IThumbnailLoader Icons { get; } = new DesignThumbnailLoader();
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<CatalogCardRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<CatalogCardRow>(_ => { });
    public ReactiveCommand<CatalogCardRow, Unit> OpenCommand { get; } = ReactiveCommand.Create<CatalogCardRow>(_ => { });

    public CatalogDesignViewModel() : base(new DesignWindowManager()) { }
}
