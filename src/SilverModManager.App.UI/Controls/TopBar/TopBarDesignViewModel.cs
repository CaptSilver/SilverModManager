using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Observable = R3.Observable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace SilverModManager.App.UI.Controls.TopBar;

public partial class TopBarDesignViewModel : AViewModel<ITopBarViewModel>, ITopBarViewModel
{
    [Reactive] public partial bool IsLoggedIn { get; set; } = true;
    [Reactive] public partial UserRole UserRole { get; set; } = UserRole.Free;
    [Reactive] public partial string? Username { get; set; } = "insomnious";
    [Reactive] public partial string AccountTooltip { get; set; } = "Logged in to Nexus Mods as insomnious";
    [Reactive] public partial IImage Avatar { get; set; } = new Bitmap(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/cyberpunk_game.png")));
    [Reactive] public partial string ActiveWorkspaceTitle { get; set; } = "Home";
    [Reactive] public partial string ActiveWorkspaceSubtitle { get; set; } = "Loadout A";
    [Reactive] public partial IAddPanelDropDownViewModel AddPanelDropDownViewModel { get; set; } = new AddPanelDropDownDesignViewModel();

    public ReactiveUI.ReactiveCommand<Unit, Unit> NewTabCommand => ReactiveCommand.Create<Unit, Unit>(_ => Unit.Default);
    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> OpenSettingsCommand => ReactiveCommand.Create<NavigationInformation, Unit>(_ => Unit.Default);

    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> ViewChangelogCommand => ReactiveCommand.Create<NavigationInformation, Unit>(_ => Unit.Default);
    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> ViewAboutCommand => ReactiveCommand.Create<NavigationInformation, Unit>(_ => Unit.Default);
    public ReactiveUI.ReactiveCommand<Unit, Unit> ViewAppLogsCommand => Initializers.DisabledReactiveCommand;
    public ReactiveUI.ReactiveCommand<Unit, Unit> ShowWelcomeMessageCommand => Initializers.EnabledReactiveCommand;
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenGitHubCommand => ReactiveCommand.Create(() => {});
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenStatusPageCommand => ReactiveCommand.Create(() => {});

    public R3.ReactiveCommand<R3.Unit, R3.Unit> LoginCommand { get; set; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> LogoutCommand { get; set; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsProfileCommand => Initializers.DisabledReactiveCommand;
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsPremiumCommand => Initializers.EnabledReactiveCommand;
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsAccountSettingsCommand => Initializers.DisabledReactiveCommand;
    
    public IPanelTabViewModel? SelectedTab { get; set; }

    public TopBarDesignViewModel()
    {
        LogoutCommand = ReactiveCommand.Create(ToggleLogin, this.WhenAnyValue(vm => vm.IsLoggedIn));
        LoginCommand = ReactiveCommandExtensions.ToReactiveCommand<R3.Unit, R3.Unit>(Observable.ToObservable(this.WhenAnyValue(vm => vm.IsLoggedIn).Select(x => !x)), convert:
            _ =>
            {
                ToggleLogin();
                return R3.Unit.Default;
            });
    }

    private void ToggleLogin()
    {
        IsLoggedIn = !IsLoggedIn;
    }
}
