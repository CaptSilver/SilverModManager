using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Windows;
using ReactiveUI;

namespace SilverModManager.Games.FOMOD.UI;

public partial class GuidedInstallerWindow : ReactiveWindow<IGuidedInstallerWindowViewModel>
{
    // Tests swap this out: a headless window can't be moved.
    internal Action<PointerPressedEventArgs> BeginMove;

    public GuidedInstallerWindow()
    {
        // On X11/XWayland the extend-client-area hints do nothing, so the window manager kept drawing its own title
        // bar above this one. Drop the decorations and move and resize the window ourselves, as dialogs do.
        SystemDecorations = SystemDecorations.None;
        BeginMove = BeginMoveDrag;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        InitializeComponent();

        Titlebar.PointerPressed += OnTitlebarPointerPressed;
        WindowChrome.AttachResizeGrips(this);

#if DEBUG
        this.AttachDevTools();
#endif

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.WindowName, view => view.Title)
                .DisposeWith(disposables);
            
            this.OneWayBind(ViewModel, vm => vm.WindowName, view => view.TitleTextBlock.Text, s => $"Install Options - {s}")
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ActiveStepViewModel, view => view.StepViewHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CloseCommand.IsExecuting)
                .SelectMany(e => e)
                .Where(e => e)
                .SubscribeWithErrorLogging(logger: default, _ => Close())
                .DisposeWith(disposables);
        });
    }

    private void OnTitlebarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Handled || !WindowChrome.IsEmptyDragSurface(e.Source as Visual, Titlebar)) return;

        BeginMove(e);
        e.Handled = true;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

