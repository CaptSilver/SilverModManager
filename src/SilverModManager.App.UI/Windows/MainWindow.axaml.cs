using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Extensions;
using ReactiveUI;

namespace SilverModManager.App.UI.Windows;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {        
        // On X11/XWayland the extend-client-area hints do nothing, so the window manager keeps
        // drawing its own title bar above the app's top bar. Drop the decorations entirely and
        // supply move, maximize and resize ourselves (see WindowChrome).
        SystemDecorations = SystemDecorations.None;

        InitializeComponent();

        WindowChrome.AttachResizeGrips(this);
        TopBarBorder.PointerPressed += OnTopBarPointerPressed;

        _ = new MainWindowDropTarget(this, DropOverlay, DropOverlayText, paths => ViewModel?.HandleDroppedFilesAsync(paths) ?? Task.CompletedTask);

        #if DEBUG
        this.AttachDevTools();
        #endif

        this.WhenActivated(disposables =>
        {
            ViewModel?.Glass.Attach(this, "SurfaceBase").DisposeWith(disposables);

            var storageProvider = GetTopLevel(this)?.StorageProvider;
            var clipboard = GetTopLevel(this)?.Clipboard;
            
            if (storageProvider is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterStorageProvider.Execute(storageProvider).Subscribe();
                    })
                    .DisposeWith(disposables);
            }
            
            if (clipboard is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterClipboard.Execute(clipboard).Subscribe();
                    })
                    .DisposeWith(disposables);
            }

            this.OneWayBind(ViewModel, vm => vm.TopBar, v => v.TopBarViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Spine, v => v.SpineViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.DevelopmentBuildBanner, v => v.DevelopmentBuildBanner.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.SecretStoreNotice, v => v.SecretStoreNotice.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.WorkspaceController.ActiveWorkspace, view => view.WorkspaceView.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.LeftMenu)
                .BindTo(this, view => view.LeftMenuViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .BindTo(this, view => view.OverlayViewHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .Select(content => content != null)
                .BindTo(this, view => view.OverlayBorder.IsVisible)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.IsActive)
                .BindTo(this, view => view.ViewModel!.IsActive)
                .DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.BringWindowToFront)
                .OnUI()
                .Where(static shouldBringToFront => shouldBringToFront)
                .Subscribe(_ => {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;

                    Activate();
                }).DisposeWith(disposables);
        });
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Handled || !WindowChrome.IsEmptyDragSurface(e.Source as Visual, TopBarBorder)) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel?.OnClose();
        base.OnClosing(e);
    }
    
    
}
