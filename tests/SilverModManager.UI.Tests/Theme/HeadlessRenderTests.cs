using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.Theme;

/// <summary>
/// Renders pages through the headless test host into a bitmap. Visual checks of the theme depend on the
/// frame having pixels in it, which the headless drawing mode does not promise.
/// </summary>
public class HeadlessRenderTests : AUiTest
{
    private const int Width = 1600;
    private const int Height = 1000;

    // Set to keep the renders somewhere a person can look at them; otherwise they live in a scratch folder that goes away.
    private const string OutputVariable = "SILVER_RENDER_OUT";

    private readonly string? _keepDirectory = Environment.GetEnvironmentVariable(OutputVariable);
    private readonly string _outputDirectory;

    public HeadlessRenderTests(IServiceProvider provider) : base(provider)
    {
        _outputDirectory = _keepDirectory ?? TestScratch.Combine("render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDirectory);
    }

    [Fact]
    public async Task Test_MyGamesRendersWithPixelVariance()
    {
        var path = await Render("MyGames", () => new MyGamesView { DataContext = new MyGamesDesignViewModel() });

        AssertHasVariance(path);
    }

    [Fact]
    public async Task Test_SettingsRendersWithPixelVariance()
    {
        var path = await Render("Settings", () => new SettingsView
        {
            DataContext = ActivatorUtilities.CreateInstance<SettingsPageViewModel>(Provider, Substitute.For<IWindowManager>()),
        });

        AssertHasVariance(path);
    }

    [Fact]
    public async Task Test_LoadoutRendersWithPixelVariance()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Render loadout");

        var path = await Render("Loadout", () => new LoadoutView
        {
            DataContext = new LoadoutViewModel(Substitute.For<IWindowManager>(), Provider, loadout.LoadoutId),
        });

        AssertHasVariance(path);
    }

    [Fact]
    public async Task Test_PictogramsRenderWithPixelVariance()
    {
        var path = await Render("Pictograms", () =>
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(32) };
            foreach (var icon in new[]
            {
                IconValues.PictogramHealth, IconValues.PictogramLibrary, IconValues.PictogramDownload, IconValues.PictogramUpload,
                IconValues.PictogramSettings, IconValues.PictogramSuccess, IconValues.PictogramCelebrate, IconValues.PictogramBox2,
                IconValues.PictogramGame3D, IconValues.PictogramCollection3D, IconValues.PictogramPlaylistAdd,
            })
            {
                panel.Children.Add(new UnifiedIcon { Value = icon, Size = 96, Margin = new Thickness(16) });
            }
            return panel;
        });

        AssertHasVariance(path);
    }

    [Fact]
    public async Task Test_MainWindowLayersRaisedPanelsOnTheDarkBase()
    {
        var path = Path.Combine(_outputDirectory, "Window-layered.png");
        Color baseColour = default, raised = default;
        var pixels = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new MainWindow { Width = Width, Height = Height });
            // The overlay scrim is only shown while an overlay is open, which a window without a view model never binds.
            window.OverlayBorder.IsVisible = false;
            ShowRendered(window);
            Render(window);
            baseColour = ThemeColour(window, "SurfaceBase");
            raised = ThemeColour(window, "SurfaceLow");

            using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height));
            bitmap.Render(window);
            bitmap.Save(path);

            Color At(int x, int y)
            {
                var buffer = new byte[4];
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
                finally { handle.Free(); }
                // BGRA
                return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
            }

            // Spine is 72 wide, the left menu 232; gutters are 6.
            return (Spine: At(30, 500), Gutter: At(72 + 2, 500), LeftMenu: At(72 + 100, 500), Page: At(800, 500),
                Strip: At(800, Height - 20), BelowLeftMenu: At(72 + 100, Height - 20));
        });

        pixels.Spine.Should().Be(baseColour);
        pixels.Gutter.Should().Be(baseColour);
        pixels.LeftMenu.Should().Be(raised);
        pixels.Page.Should().Be(raised);
        pixels.BelowLeftMenu.Should().Be(raised, "the left menu runs to the bottom of the window");
        pixels.Strip.Should().Be(baseColour, "the bottom strips sit on the window base under the page");
    }

    private static Color ThemeColour(Control control, string key)
    {
        control.TryFindResource(key, control.ActualThemeVariant, out var resource).Should().BeTrue();
        return (Color)resource!;
    }

    private async Task<string> Render(string name, Func<Control> createContent)
    {
        var path = Path.Combine(_outputDirectory, name + ".png");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new Window { Width = Width, Height = Height, Content = createContent() });
            ShowRendered(window);
            // Items controls realise their rows on a later job, so lay out and tick once more before capturing.
            Render(window);

            using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height));
            bitmap.Render(window);
            bitmap.Save(path);
        });
        return path;
    }

    private static void AssertHasVariance(string path)
    {
        using var bitmap = new Bitmap(path);
        var buffer = new byte[Width * Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), buffer.Length, Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var colours = new HashSet<uint>();
        var opaque = 0;
        for (var i = 0; i < buffer.Length; i += 4)
        {
            colours.Add(BitConverter.ToUInt32(buffer, i));
            if (buffer[i + 3] != 0) opaque++;
        }

        opaque.Should().BeGreaterThan(0, "an all-transparent frame means nothing was drawn");
        colours.Count.Should().BeGreaterThan(8, "a blank frame is one flat colour");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_keepDirectory is null && Directory.Exists(_outputDirectory)) Directory.Delete(_outputDirectory, recursive: true);
    }
}
