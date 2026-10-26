using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using ColorTextBlock.Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using NexusMods.Hashing.xxHash3;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Snapshots;

/// <summary>
/// Pixel snapshots of three pages, so a theme change shows up as a diff instead of being noticed by eye later.
/// </summary>
/// <remarks>
/// <para>
/// The committed <c>*.verified.png</c> files are the baseline. They are re-accepted whenever the palette changes on
/// purpose, and after any intentional change to one of these pages. To re-accept, run the tests, look at each
/// <c>*.received.png</c> next to its verified file, then either run with <c>VERIFY_ACCEPT=true</c> or move the
/// received file over the verified one. A missing verified file fails the run the same way, so the first run
/// of a new snapshot always produces a received file to inspect before accepting it.
/// </para>
/// <para>
/// Comparison goes through Verify.ImageMagick with a small tolerance, so text anti-aliasing that differs
/// between machines does not fail the run. Every frame is checked for pixel variance before it is compared: a
/// blank frame would otherwise be a perfectly good baseline.
/// </para>
/// </remarks>
public class PageSnapshotTests(IServiceProvider provider) : AUiTest(provider)
{
    private const int Width = 1600;
    private const int Height = 1000;

    // The project-wide 0.005 is averaged over the whole page, which let the Loadout page's empty state pass
    // against a frame with two mod rows. Repeat renders on one machine come out identical; the margin is for
    // font rasterisation on another, and painting out one pictogram or one table row moves the difference past 0.005.
    private const double Threshold = 0.0005;

    [Fact]
    public async Task Test_MyGames()
    {
        var png = await Capture(() => new MyGamesView { DataContext = new MyGamesDesignViewModel() });
        await VerifyPng(png);
    }

    [Fact]
    public async Task Test_Settings()
    {
        // The default and the choices both follow the machine's processor count, so pin it to a value every
        // machine offers and put the old one back for the tests that share this settings store.
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var parallelDownloads = settings.Get<DownloadSettings>().MaxParallelDownloads;
        settings.Update<DownloadSettings>(_ => new DownloadSettings { MaxParallelDownloads = 16 });
        try
        {
            var png = await Capture(() => new SettingsView
            {
                DataContext = ActivatorUtilities.CreateInstance<SettingsPageViewModel>(Provider, Substitute.For<IWindowManager>()),
            }, beforeCapture: PinTestScratchPaths);
            await VerifyPng(png);
        }
        finally
        {
            settings.Update<DownloadSettings>(_ => new DownloadSettings { MaxParallelDownloads = parallelDownloads });
        }
    }

    [Fact]
    public async Task Test_Loadout()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Snapshot loadout");
        // Separate commits give the mods distinct install times, so the page's newest-first order is fixed.
        await SeedLocalMod(loadout, "Gunmetal Textures");
        await SeedLocalMod(loadout, "Silver Sounds");

        var png = await Capture(() => new LoadoutView
        {
            DataContext = new LoadoutViewModel(Substitute.For<IWindowManager>(), Provider, loadout.LoadoutId),
        }, isReady: window => DateComponentsIn(window).Length == 2, beforeCapture: PinInstallDates);
        await VerifyPng(png);
    }

    /// <summary>A mod added from a local archive, which is the simplest kind of row the loadout page lists.</summary>
    private async Task SeedLocalMod(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var item = default(LibraryItem.New);
        var file = new LibraryFile.New(tx, out var libraryId)
        {
            FileName = name + ".zip",
            Hash = Hash.From((ulong)name.Length),
            Size = NexusMods.Paths.Size.From(1024),
            LibraryItem = item = new LibraryItem.New(tx, libraryId) { Name = name },
        };
        _ = new LocalFile.New(tx, libraryId) { OriginalPath = "/mods/" + name + ".zip", LibraryFile = file };
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, groupId) { LibraryItemId = item, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private static DateComponent[] DateComponentsIn(Window window) =>
        window.GetVisualDescendants().OfType<Control>().Select(c => c.DataContext).OfType<DateComponent>().Distinct().ToArray();

    /// <summary>
    /// The install column reads how long ago the mod was added ("now", "one second ago"), which depends on how
    /// quickly the frame follows the seeding.
    /// </summary>
    private static void PinInstallDates(Window window)
    {
        var dates = DateComponentsIn(window);
        dates.Should().HaveCount(2, "both seeded mods' install dates have to be pinned");
        foreach (var date in dates) date.FormattedValue.Value = "2 days ago";

        // An auto column keeps the widest text it has seen, and before pinning the dates read "now" or "one second
        // ago" depending on timing, which moved every column after this one between runs.
        var grid = window.GetVisualDescendants().OfType<Avalonia.Controls.TreeDataGrid>().Single();
        var columns = grid.Source!.Columns;
        var installed = columns.Select((column, index) => (column, index)).Single(c => c.column.Header as string == "Installed").index;
        columns.SetColumnWidth(installed, new GridLength(120));
    }

    /// <summary>
    /// The storage location row shows the test host's per-run scratch folder, which differs on every run.
    /// </summary>
    private static void PinTestScratchPaths(Window window)
    {
        // The description is rendered markdown, so the path is a run inside a CTextBlock's content.
        var blocks = window.GetVisualDescendants().OfType<CTextBlock>()
            .Where(t => RunsIn(t.Content).Any(r => r.Text?.EndsWith("/Archives") == true))
            .ToArray();
        blocks.Should().NotBeEmpty("the storage location path has to be found to be pinned");
        foreach (var block in blocks)
        {
            foreach (var run in RunsIn(block.Content).Where(r => r.Text?.EndsWith("/Archives") == true))
                run.Text = "/home/user/.local/share/SilverModManager/Archives";
            // The block lays its text out once and keeps it, so it has to be told the content changed.
            block.InvalidateMeasure();
            block.InvalidateVisual();
        }
    }

    private static IEnumerable<CRun> RunsIn(IEnumerable<CInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is CRun run) yield return run;
            else if (inline is CSpan span)
                foreach (var nested in RunsIn(span.Content)) yield return nested;
        }
    }

    /// <param name="isReady">For pages that fill in from the database after they are shown: waited for before capturing.</param>
    /// <param name="beforeCapture">Replaces run-dependent content, after the page is ready and right before the frame is taken.</param>
    private async Task<byte[]> Capture(Func<Control> createContent, Func<Window, bool>? isReady = null, Action<Window>? beforeCapture = null)
    {
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new Window { Width = Width, Height = Height, Content = createContent() });
            // Item views are hosted in ViewModelViewHost, which cross-fades its content in on wall-clock time, so a
            // capture could land mid-fade with every row partly transparent.
            window.Styles.Add(new Style(x => x.OfType<TransitioningContentControl>())
            {
                Setters = { new Setter(TransitioningContentControl.PageTransitionProperty, null) },
            });
            ShowRendered(window);
            // Items controls realise their rows on a later job, so lay out and tick once more before capturing.
            Render(window);
            return window;
        });

        if (isReady is not null)
        {
            await EventuallyOnUi(() =>
            {
                Render(window);
                isReady(window).Should().BeTrue("the page has to finish loading before it is captured");
            });
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (beforeCapture is not null)
            {
                beforeCapture(window);
                Render(window);
            }

            using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        });
    }

    private static Task VerifyPng(byte[] png, [CallerMemberName] string name = "", [CallerFilePath] string sourceFile = "")
    {
        AssertHasVariance(png);
        // ReSharper disable once ExplicitCallerInfoArgument
        return Verify(new MemoryStream(png), extension: "png", sourceFile: sourceFile).UseMethodName(name).DisableDiff()
            .ImageMagickComparer(Threshold, ErrorMetric.Fuzz);
    }

    private static void AssertHasVariance(byte[] png)
    {
        using var bitmap = new Bitmap(new MemoryStream(png));
        var buffer = new byte[Width * Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), buffer.Length, Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var colours = new HashSet<uint>();
        for (var i = 0; i < buffer.Length; i += 4) colours.Add(BitConverter.ToUInt32(buffer, i));

        colours.Count.Should().BeGreaterThan(8, "a blank frame is one flat colour, not a baseline");
    }
}
