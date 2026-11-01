using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NexusMods.Paths;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>Drag events over a window with a drop target: when the overlay shows, and which drops reach the window.</summary>
[Collection(HeadlessUi.Collection)]
public class MainWindowDropTargetTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly string _root = Directory.CreateTempSubdirectory("window-drop-target-").FullName;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private sealed record Harness(Window Window, Border First, Border Second, Border Inbox, Border Overlay, TextBlock OverlayText, Border Caption, TextBlock CaptionText, IMainWindowDropActions Actions);

    private static Border Area() => new() { Background = Brushes.Gray, Width = 100, Height = 100 };

    // The inbox takes its own drops, as the modlist page's drop zone does.
    private sealed class InboxTarget : IDropTarget
    {
        public DropVerdict Inspect(DragPayload payload)
            => payload is DragPayload.DroppedFiles ? new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 file to the inbox") : new DropVerdict.Ignore();

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private async Task<Harness> Show()
    {
        return await OnUi(() =>
        {
            var first = Area();
            var second = Area();
            var inbox = Area();
            var overlayText = new TextBlock();
            var overlay = new Border { Child = overlayText, IsHitTestVisible = false, IsVisible = false };
            var captionText = new TextBlock();
            var caption = new Border { Child = captionText, IsHitTestVisible = false, IsVisible = false };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { first, second, inbox } };
            var window = Track(new Window { Width = 400, Height = 200, Content = new Grid { Children = { row, overlay, caption } } });

            var actions = Substitute.For<IMainWindowDropActions>();
            actions.AddMod(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var errors = Substitute.For<IErrorPresenter>();
            var feedback = new WindowDropFeedback(overlay, overlayText, caption, captionText);
            var target = new FilesDropTarget(actions, Substitute.For<IWindowNotificationService>(), logger: null);
            _ = new DropZone(window, target, errors, feedback, outer: true);
            _ = new DropZone(inbox, new InboxTarget(), errors, feedback);

            ShowRendered(window);
            return Task.FromResult(new Harness(window, first, second, inbox, overlay, overlayText, caption, captionText, actions));
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
            harness.Caption.IsVisible.Should().BeTrue();
            harness.CaptionText.Text.Should().Be("Add 1 file to the inbox");
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

            harness.Actions.ReceivedCalls().Should().BeEmpty();
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

            harness.Actions.Received(1).AddMod(Arg.Is<AbsolutePath>(p => p.GetFullPath() == path), Arg.Any<CancellationToken>());
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
