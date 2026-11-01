using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The modlist page as a drop zone inside a window zone: the inbox takes files, and lets everything else fall through.</summary>
public class WabbajackInboxZoneTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class WindowTarget : IDropTarget
    {
        public List<DragPayload> Inspected { get; } = [];
        public List<DragPayload> Dropped { get; } = [];

        public DropVerdict Inspect(DragPayload payload)
        {
            Inspected.Add(payload);
            return new DropVerdict.Accept(DragDropEffects.Copy, "Add to the library");
        }

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Dropped.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(Window Window, WabbajackModlistView View, WabbajackModlistDesignViewModel Page, WindowTarget WindowTarget);

    private async Task<Harness> Show()
    {
        var harness = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var page = new WabbajackModlistDesignViewModel();
            var view = new WabbajackModlistView { DataContext = page };
            var window = Track(new Window { Width = 1200, Height = 900, Content = view });
            var windowTarget = new WindowTarget();
            new DropZone(window, windowTarget, Substitute.For<IErrorPresenter>(), outer: true);
            ShowRendered(window);
            return new Harness(window, view, page, windowTarget);
        });
        await EventuallyOnUi(() =>
        {
            Render(harness.Window);
            harness.View.FindControl<Control>("Body")!.IsEffectivelyVisible.Should().BeTrue();
            DragDrop.GetAllowDrop(harness.View.FindControl<Control>("Body")!).Should().BeTrue("activation wires the zone");
        });
        return harness;
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

    private static IDataObject Mods() => DragPayloads.ToDataObject(new DragPayload.LoadoutMods(LoadoutId.From(EntityId.From(1)), []));

    private static void Raise(Harness harness, RawDragEventType type, IDataObject data, DragDropEffects effects = DragDropEffects.Copy)
    {
        var body = harness.View.FindControl<Control>("Body")!;
        var centre = body.TranslatePoint(new Point(body.Bounds.Width / 2, body.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.DragDrop(centre, type, data, effects);
    }

    [Fact]
    public async Task AFileDroppedOnThePage_GoesToTheInboxAndNotToTheWindow()
    {
        var harness = await Show();
        var path = Path.Combine(Path.GetTempPath(), $"inbox-zone-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(path, "x");
        try
        {
            var data = Files(path);
            await OnUi(() =>
            {
                Raise(harness, RawDragEventType.DragEnter, data);
                Raise(harness, RawDragEventType.Drop, data);
            });

            harness.Page.DroppedFiles.Should().ContainSingle().Which.Should().Equal(path);
            harness.WindowTarget.Dropped.Should().BeEmpty("the page's zone handled the drop");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ADragOfMods_IsLeftUnhandledByThePage()
    {
        var harness = await Show();
        await OnUi(() => Raise(harness, RawDragEventType.DragOver, Mods(), DragDropEffects.Move));

        // The window zone is outer: it skips events an inner zone handled, so it only inspects if the page's zone ignored them.
        harness.WindowTarget.Inspected.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LoadoutMods>();
        harness.Page.DroppedFiles.Should().BeEmpty();
    }
}
