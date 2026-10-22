using System.Reactive;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Thunderstore;

public class ThunderstoreDesignViewModel : APageViewModel<IThunderstoreViewModel>, IThunderstoreViewModel
{
    public LoadoutId LoadoutId { get; set; }
    public string SearchText { get; set; } = "";
    public bool ShowDeprecated { get; set; }

    public IReadOnlyList<ThunderstorePackageRow> Packages { get; } =
    [
        new("BepInEx-BepInExPack", "5.4.2305", 1500, false, "Unity mod loader", new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/")),
        new("Zehs-REPOLib", "4.2.0", 900, false, "Library for R.E.P.O. mod developers", new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/")),
        new("Old-Abandoned", "0.3.1", 12, true, "No longer maintained", null),
    ];

    public string ErrorText => "";
    public bool IsRefreshing => false;
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });

    public ThunderstoreDesignViewModel() : base(new DesignWindowManager()) { }
}
