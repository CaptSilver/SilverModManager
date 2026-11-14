using Avalonia.Controls;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Input.Menu;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>Menu opens the app menu. It does nothing while one is already open, so a second press does not stack another.</summary>
public sealed class MenuBinding(Func<TopLevel, (ITopBarViewModel TopBar, ILeftMenuViewModel? LeftMenu)?>? source = null) : PadBinding
{
    private readonly Func<TopLevel, (ITopBarViewModel TopBar, ILeftMenuViewModel? LeftMenu)?> _source = source ?? FromMainWindow;

    public override GamepadButton Button => GamepadButton.Menu;

    public override string Label => "Menu";

    public override bool CanExecute(FocusContext ctx) => ctx.Overlays.CurrentOverlay is not IAppMenuOverlayViewModel && _source(ctx.Root) is not null;

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (_source(ctx.Root) is not var (topBar, leftMenu)) return;

        ctx.Overlays.Enqueue(new AppMenuOverlayViewModel(AppMenuItems.Build(topBar, leftMenu)));
    }

    private static (ITopBarViewModel, ILeftMenuViewModel?)? FromMainWindow(TopLevel root) =>
        root is MainWindow { ViewModel: { } vm } ? (vm.TopBar, vm.LeftMenu) : null;
}
