using SilverModManager.App.UI.Controls.Cards;
using System.Reactive;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Thunderstore;

public class ThunderstoreDesignViewModel : APageViewModel<IThunderstoreViewModel>, IThunderstoreViewModel
{
    public LoadoutId LoadoutId { get; set; }
    public string SearchText { get; set; } = "";
    public bool ShowDeprecated { get; set; }

    public IReadOnlyList<ThunderstorePackageRow> Packages { get; } =
    [
        new("BepInEx-BepInExPack", "5.4.2305", 1500, false, "Unity mod loader", new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/"), Owner: "BepInEx", DisplayName: "BepInExPack", Downloads: 4_200_000),
        new("Zehs-REPOLib", "4.2.0", 900, false, "Library for R.E.P.O. mod developers", new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/"), Owner: "Zehs", DisplayName: "REPOLib", Downloads: 830_000),
        new("Old-Abandoned", "0.3.1", 12, true, "No longer maintained", null, Owner: "Old", DisplayName: "Abandoned", Downloads: 900),
    ];

    public IThumbnailLoader Icons { get; } = new DesignThumbnailLoader();
    public string ErrorText => "";
    public bool IsRefreshing => false;
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });

    public ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });

    public ThunderstoreDesignViewModel() : base(new DesignWindowManager()) { }
}
