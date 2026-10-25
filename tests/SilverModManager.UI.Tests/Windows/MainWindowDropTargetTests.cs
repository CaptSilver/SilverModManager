using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>Drag events over a window with a drop target: when the overlay shows, and which drops reach the window.</summary>
public class MainWindowDropTargetTests(IServiceProvider provider) : AUiTest(provider), IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("window-drop-target-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record Harness(Window Window, Border First, Border Second, Border Inbox, Border Overlay, TextBlock OverlayText, List<IReadOnlyList<string>> Drops);

    private static Border Area() => new() { Background = Brushes.Gray, Width = 100, Height = 100 };

    // The inbox takes its own drops, as the modlist page's drop zone does.
    private static Border Inbox()
    {
        var inbox = Area();
        inbox.AddHandler(DragDrop.DragEnterEvent, (_, e) => e.Handled = true);
        inbox.AddHandler(DragDrop.DragOverEvent, (_, e) => e.Handled = true);
        inbox.AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = true);
        return inbox;
    }

    private async Task<Harness> Show()
    {
        return await OnUi(() =>
        {
            var first = Area();
            var second = Area();
            var inbox = Inbox();
            var overlayText = new TextBlock();
            var overlay = new Border { Child = overlayText, IsHitTestVisible = false, IsVisible = false };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { first, second, inbox } };
            var window = new Window { Width = 400, Height = 200, Content = new Grid { Children = { row, overlay } } };

            var drops = new List<IReadOnlyList<string>>();
            _ = new MainWindowDropTarget(window, overlay, overlayText, paths =>
            {
                drops.Add(paths);
                return Task.CompletedTask;
            });

            window.Show();
            window.UpdateLayout();
            // Drag events find their target by hit testing the rendered scene, which headless only updates on a render tick.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            return Task.FromResult(new Harness(window, first, second, inbox, overlay, overlayText, drops));
        });
    }

    private string ModArchive(string name = "mod.zip")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static IDataObject Files(params string[] paths)
    {
        var items = paths.Select(path =>
        {
            var file = Substitute.For<IStorageFile>();
            file.Path.Returns(new Uri(path));
            file.Name.Returns(Path.GetFileName(path));
            return (IStorageItem)file;
        }).ToArray();

        var data = new DataObject();
        data.Set(DataFormats.Files, items);
        return data;
    }

    private static Point CentreOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Raise(Harness harness, Control over, RawDragEventType type, IDataObject data) =>
        harness.Window.DragDrop(CentreOf(over, harness.Window), type, data, DragDropEffects.Copy);

    [Fact]
    public async Task EnteringTheWindow_ShowsWhatTheFilesWillDo()
    {
        var harness = await Show();
        var data = Files(ModArchive());

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);

            harness.Overlay.IsVisible.Should().BeTrue();
            harness.OverlayText.Text.Should().Be("Add 1 mod to the library");
        });
    }

    [Fact]
    public async Task MovingFromOneControlToTheNext_KeepsTheOverlayShowing()
    {
        var harness = await Show();
        var data = Files(ModArchive());

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);
            Raise(harness, harness.First, RawDragEventType.DragOver, data);
            Raise(harness, harness.Second, RawDragEventType.DragOver, data);

            harness.Overlay.IsVisible.Should().BeTrue();
        });
    }

    [Fact]
    public async Task OverAControlThatTakesItsOwnDrops_TheOverlayHides()
    {
        var harness = await Show();
        var data = Files(ModArchive());

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inbox, RawDragEventType.DragOver, data);
            Raise(harness, harness.Inbox, RawDragEventType.DragOver, data);

            harness.Overlay.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task ADropOnAControlThatTakesItsOwnDrops_DoesNotReachTheWindow()
    {
        var harness = await Show();
        var data = Files(ModArchive());

        await OnUi(() =>
        {
            Raise(harness, harness.Inbox, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inbox, RawDragEventType.Drop, data);

            harness.Drops.Should().BeEmpty();
            harness.Overlay.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task ADropElsewhere_HandsTheFilesOnAndHidesTheOverlay()
    {
        var harness = await Show();
        var path = ModArchive();
        var data = Files(path);

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);
            Raise(harness, harness.First, RawDragEventType.Drop, data);

            harness.Drops.Should().ContainSingle().Which.Should().Equal(path);
            harness.Overlay.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task LeavingTheWindow_HidesTheOverlay()
    {
        var harness = await Show();
        var data = Files(ModArchive());

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);
            Raise(harness, harness.First, RawDragEventType.DragLeave, data);

            harness.Overlay.IsVisible.Should().BeFalse();
        });
    }

    // Classifying opens every file, and every zip as an archive, so it must not happen on each mouse move.
    [Fact]
    public async Task DraggingTheSameFiles_ClassifiesThemOncePerDrag()
    {
        var harness = await Show();
        var path = ModArchive();
        var data = Files(path);

        await OnUi(() =>
        {
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);
            File.Delete(path);
            Raise(harness, harness.First, RawDragEventType.DragOver, data);
            Raise(harness, harness.Second, RawDragEventType.DragOver, data);

            harness.OverlayText.Text.Should().Be("Add 1 mod to the library");

            Raise(harness, harness.Second, RawDragEventType.Drop, data);
            Raise(harness, harness.First, RawDragEventType.DragEnter, data);

            harness.OverlayText.Text.Should().Be("Can't add mod.zip");
        });
    }
}
