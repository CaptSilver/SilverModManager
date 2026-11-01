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
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Tests.Framework;
using static SilverModManager.UI.Tests.LeftMenu.LeftMenuTestSupport;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>Dragging over a collection entry of the left menu: the entry's own drop target answers, and the feedback sits on the entry.</summary>
[Collection(HeadlessUi.Collection)]
public class LeftMenuDropTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class RecordingTarget(DropVerdict verdict) : IDropTarget
    {
        public List<DragPayload> Dropped { get; } = [];
        public DropVerdict Inspect(DragPayload payload) => payload is DragPayload.LoadoutMods ? verdict : new DropVerdict.Ignore();

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Dropped.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class WindowTarget : IDropTarget
    {
        public DropVerdict Inspect(DragPayload payload) => payload is DragPayload.DroppedFiles
            ? new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 mod to the library")
            : new DropVerdict.Ignore();

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Harness(
        Window Window,
        LoadoutLeftMenuView View,
        Control Overlay,
        TextBlock OverlayText,
        Border Caption,
        TextBlock CaptionText,
        RecordingTarget NcrCore,
        RecordingTarget Neighbour);

    private async Task<Harness> Show()
    {
        var controller = Controller();
        var page = new PageData { FactoryId = PageFactoryId.From(Guid.NewGuid()), Context = Substitute.For<IPageFactoryContext>() };
        var errors = Substitute.For<IErrorPresenter>();
        var ncrCore = new RecordingTarget(new DropVerdict.Accept(DragDropEffects.Move, "Move 3 mods to NCR Core"));
        var neighbour = new RecordingTarget(new DropVerdict.Refuse("Neighbour is a Nexus collection; it decides what it contains"));

        LeftMenuItemViewModel Entry(string text, IDropTarget? target) => new(controller, WorkspaceId.From(Guid.NewGuid()), page)
        {
            Text = new StringComponent(text),
            Icon = IconValues.Download,
            DropTarget = target,
            ErrorPresenter = errors,
        };

        var menu = new LoadoutLeftMenuStub(Entry("My Mods", null), Entry("NCR Core", ncrCore), Entry("Neighbour", neighbour));
        return await OnUi(async () =>
        {
            var overlay = new Border { IsVisible = false };
            var overlayText = new TextBlock();
            var captionText = new TextBlock();
            var caption = new Border { IsVisible = false, Child = captionText, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var view = new LoadoutLeftMenuView { ViewModel = menu, Width = 260 };
            var window = Track(new Window { Width = 800, Height = 900, Content = new Grid { Children = { view, overlay, caption } } });
            DropFeedback.SetLayer(window, new WindowDropFeedback(overlay, overlayText, caption, captionText));
            new DropZone(window, new WindowTarget(), errors, outer: true);
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);
            return new Harness(window, view, overlay, overlayText, caption, captionText, ncrCore, neighbour);
        });
    }

    private static IReadOnlyList<Control> Rows(LoadoutLeftMenuView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().Single(items => items.Name == "CollectionItems").ItemsPanelRoot!.Children;

    private static LeftMenuNavButton ButtonOf(Control row) => row.GetVisualDescendants().OfType<LeftMenuNavButton>().First();

    private static void Raise(Harness harness, int row, RawDragEventType type, IDataObject data, DragDropEffects effects = DragDropEffects.Move)
    {
        var control = Rows(harness.View)[row];
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.DragDrop(centre, type, data, effects);
    }

    private static IDataObject Mods() => DragPayloads.ToDataObject(new DragPayload.LoadoutMods(LoadoutId.From(EntityId.From(1)), []));

    private static IDataObject Files()
    {
        var file = Substitute.For<IStorageFile>();
        file.Path.Returns(new Uri("/tmp/a.zip"));
        file.Name.Returns("a.zip");
        var data = new DataObject();
        data.Set(DataFormats.Files, new IStorageItem[] { file });
        return data;
    }

    private static bool IsDropOver(Control row) => ButtonOf(row).Classes.Contains(":drop-over");

    [Fact]
    public async Task ModsOverAnEntry_HighlightTheEntryAndCaptionTheVerdict()
    {
        var harness = await Show();

        await OnUi(() => Raise(harness, 1, RawDragEventType.DragEnter, Mods()));

        await OnUi(() =>
        {
            IsDropOver(Rows(harness.View)[1]).Should().BeTrue();
            IsDropOver(Rows(harness.View)[2]).Should().BeFalse();
            harness.Caption.IsVisible.Should().BeTrue();
            harness.CaptionText.Text.Should().Be("Move 3 mods to NCR Core");
            harness.Overlay.IsVisible.Should().BeFalse("only the window's own zone dims the whole window");
        });
    }

    [Fact]
    public async Task LeavingTheEntry_ClearsTheHighlightAndTheCaption()
    {
        var harness = await Show();
        var data = Mods();

        await OnUi(() =>
        {
            Raise(harness, 1, RawDragEventType.DragEnter, data);
            Raise(harness, 1, RawDragEventType.DragLeave, data);
        });

        await OnUi(() =>
        {
            IsDropOver(Rows(harness.View)[1]).Should().BeFalse();
            harness.Caption.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task DroppingOnAnEntry_CallsThatEntrysTargetOnly()
    {
        var harness = await Show();
        var data = Mods();

        await OnUi(() =>
        {
            Raise(harness, 1, RawDragEventType.DragEnter, data);
            Raise(harness, 1, RawDragEventType.Drop, data);
        });

        harness.NcrCore.Dropped.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LoadoutMods>();
        harness.Neighbour.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusingEntry_ShowsItsReasonAndTakesNoDrop()
    {
        var harness = await Show();
        var data = Mods();

        await OnUi(() =>
        {
            Raise(harness, 2, RawDragEventType.DragEnter, data);
            harness.CaptionText.Text.Should().Be("Neighbour is a Nexus collection; it decides what it contains");
            Raise(harness, 2, RawDragEventType.Drop, data);
        });

        harness.Neighbour.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task FilesOverAnEntry_FallThroughToTheWindowZone()
    {
        var harness = await Show();

        await OnUi(() => Raise(harness, 1, RawDragEventType.DragEnter, Files(), DragDropEffects.Copy));

        await OnUi(() =>
        {
            IsDropOver(Rows(harness.View)[1]).Should().BeFalse("the entry ignores files");
            harness.Overlay.IsVisible.Should().BeTrue("the window's zone took them");
            harness.OverlayText.Text.Should().Be("Add 1 mod to the library");
        });
    }

    [Fact]
    public async Task AnEntryWithoutATarget_IgnoresMods()
    {
        var harness = await Show();

        await OnUi(() => Raise(harness, 0, RawDragEventType.DragEnter, Mods()));

        await OnUi(() =>
        {
            IsDropOver(Rows(harness.View)[0]).Should().BeFalse();
            harness.Caption.IsVisible.Should().BeFalse();
        });
    }
}
