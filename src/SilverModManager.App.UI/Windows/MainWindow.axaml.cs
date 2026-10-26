using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        // Glass replaces this hint once it attaches; until then the frame still needs a transparent window to round.
        TransparencyLevelHint = GlassAppearance.HintFor(enabled: false, transparentFrame: true);
        _blurRegionTimer.Tick += (_, _) => ApplyBlurRegion();
        UpdateFrameCorners();

        WindowChrome.AttachResizeGrips(this);
        TopBarBorder.PointerPressed += OnTopBarPointerPressed;

        _ = new MainWindowDropTarget(this, DropOverlay, DropOverlayText, paths => ViewModel?.HandleDroppedFilesAsync(paths) ?? Task.CompletedTask);

        #if DEBUG
        this.AttachDevTools();
        #endif

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not null) AttachGlass(ViewModel.Glass).DisposeWith(disposables);

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

    /// <summary>Glass tints the dark window base and, more lightly, the left menu and page panels raised on it.</summary>
    public IDisposable AttachGlass(GlassAppearance glass)
    {
        _logger = glass.Logger;
        return glass.AttachFramed(this, WindowFrame, "SurfaceWindow", "SurfaceLow", LeftMenuBorder, WorkspacesBorder);
    }

    private ILogger _logger = NullLogger.Instance;

    private static readonly CornerRadius FloatingCorners = new(8);

    /// <summary>
    /// Rounded while the window floats on a compositor. Without transparency the corners would be filled black, and a
    /// maximized or fullscreen window meets the screen edges, where rounding only cuts off pixels.
    /// </summary>
    public static CornerRadius FrameCornerRadiusFor(WindowState state, WindowTransparencyLevel actual) =>
        state is WindowState.Maximized or WindowState.FullScreen || actual == WindowTransparencyLevel.None
            ? default
            : FloatingCorners;

    private void UpdateFrameCorners()
    {
        // Property changes arrive while InitializeComponent is still building the window.
        if (WindowFrame is null) return;
        WindowFrame.CornerRadius = FrameCornerRadiusFor(WindowState, ActualTransparencyLevel);
        _logger.LogTrace("Window frame corners {Radius} for {State}, transparency {Level}", WindowFrame.CornerRadius.TopLeft, WindowState, ActualTransparencyLevel);
        // KWin blurs the whole window rectangle unless told otherwise, which squares off the rounded corners.
        if (GlassAppearance.IsBlurred(ActualTransparencyLevel))
        {
            _blurRegionTimer.Stop();
            _blurRegionTimer.Start();
        }
    }

    // Avalonia writes "blur everything" on its own X connection, and X orders requests only within one connection, so
    // writing the shape at once loses whenever Avalonia's write is still queued. Waiting lets that flush first, and
    // folds the stream of size changes during a resize into one write.
    private readonly DispatcherTimer _blurRegionTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void ApplyBlurRegion()
    {
        _blurRegionTimer.Stop();
        if (GlassAppearance.IsBlurred(ActualTransparencyLevel)) BlurRegion.Apply(this, WindowFrame.CornerRadius.TopLeft, _logger);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == ActualTransparencyLevelProperty || change.Property == ClientSizeProperty)
            UpdateFrameCorners();
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
