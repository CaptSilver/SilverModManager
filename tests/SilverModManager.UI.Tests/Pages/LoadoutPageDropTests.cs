using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A collection's page is a drop zone for that collection: mods dragged over it are moved in, and the "All" page, which
/// is no collection, leaves them to the window.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class LoadoutPageDropTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>Stands in for the window's zone and records what reached it.</summary>
    private sealed class WindowTarget : IDropTarget
    {
        public List<DragPayload> Inspected { get; } = [];

        public DropVerdict Inspect(DragPayload payload)
        {
            Inspected.Add(payload);
            return new DropVerdict.Ignore();
        }

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Harness(
        Window Window,
        LoadoutView View,
        LoadoutViewModel ViewModel,
        Control Overlay,
        Border Caption,
        TextBlock CaptionText,
        WindowTarget WindowTarget);

    private async Task<EntityId> SeedMod(Loadout.ReadOnly loadout, string name, EntityId parent)
    {
        using var tx = Connection.BeginTransaction();
        var item = default(LibraryItem.New);
        var file = new LibraryFile.New(tx, out var libraryId)
        {
            FileName = name + ".zip",
            Hash = Hash.From((ulong)Guid.NewGuid().GetHashCode()),
            Size = NexusMods.Paths.Size.From(1024),
            LibraryItem = item = new LibraryItem.New(tx, libraryId) { Name = name },
        };
        _ = new LocalFile.New(tx, libraryId) { OriginalPath = "/mods/" + name + ".zip", LibraryFile = file };
        LibraryDestinations.Write(tx, libraryId, new LoadoutAcquisition(loadout.LoadoutId).Destination(Connection.Db));
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, groupId) { LibraryItemId = item, LoadoutItemGroup = group };
        tx.Add(groupId, LoadoutItem.Parent, parent);
        var result = await tx.Commit();
        return result[groupId];
    }

    private async Task<Harness> Show(Loadout.ReadOnly loadout, CollectionGroup.ReadOnly? collection)
    {
        var windowTarget = new WindowTarget();
        return await OnUi(async () =>
        {
            var viewModel = new LoadoutViewModel(
                Substitute.For<IWindowManager>(),
                Provider,
                loadout.LoadoutId,
                collection is { } group ? group.CollectionGroupId : Optional<CollectionGroupId>.None);
            var overlay = new Border { IsVisible = false };
            var overlayText = new TextBlock();
            var captionText = new TextBlock();
            var caption = new Border { IsVisible = false, Child = captionText, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var view = new LoadoutView { DataContext = viewModel };
            var window = Track(new Window { Width = 1000, Height = 800, Content = new Grid { Children = { view, overlay, caption } } });
            DropFeedback.SetLayer(window, new WindowDropFeedback(overlay, overlayText, caption, captionText));
            new DropZone(window, windowTarget, Substitute.For<IErrorPresenter>(), outer: true);
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);
            return new Harness(window, view, viewModel, overlay, caption, captionText, windowTarget);
        });
    }

    /// <summary>The drop surface: the mods tab's content, which carries the :drop-over state.</summary>
    private static Control Body(Harness harness) => harness.View.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "ModsBody");

    private static void Raise(Harness harness, RawDragEventType type, IDataObject data)
    {
        var body = Body(harness);
        var centre = body.TranslatePoint(new Point(body.Bounds.Width / 2, body.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.DragDrop(centre, type, data, DragDropEffects.Move);
    }

    private static IDataObject Mods(Loadout.ReadOnly loadout, params EntityId[] groups)
        => DragPayloads.ToDataObject(new DragPayload.LoadoutMods(loadout.LoadoutId, groups.Select(LoadoutItemId.From).ToArray()));

    private async Task<(Loadout.ReadOnly Loadout, CollectionGroup.ReadOnly Here, CollectionGroup.ReadOnly Elsewhere)> Seed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Page drop " + Guid.NewGuid());
        var here = await CollectionCreator.CreateNewCollectionGroup(Connection, loadout.LoadoutId, "Tweaks");
        var elsewhere = await CollectionCreator.CreateNewCollectionGroup(Connection, loadout.LoadoutId, "Textures");
        return (loadout, here, elsewhere);
    }

    [Fact]
    public async Task ACollectionPage_IsADropTarget_WithAnErrorPresenter()
    {
        var (loadout, here, _) = await Seed();

        var harness = await Show(loadout, here);

        harness.ViewModel.DropTarget.Should().BeOfType<CollectionDropTarget>();
        harness.ViewModel.ErrorPresenter.Should().NotBeNull("a failed drop has to be shown somewhere");
    }

    [Fact]
    public async Task TheAllPage_IsNotADropTarget()
    {
        var (loadout, _, _) = await Seed();

        var harness = await Show(loadout, null);

        harness.ViewModel.DropTarget.Should().BeNull();
    }

    [Fact]
    public async Task ModsAlreadyInTheCollection_AreRefusedByName()
    {
        var (loadout, here, _) = await Seed();
        var mod = await SeedMod(loadout, "Already here", here.Id);
        var harness = await Show(loadout, here);

        await OnUi(() => Raise(harness, RawDragEventType.DragEnter, Mods(loadout, mod)));

        await OnUi(() =>
        {
            harness.Caption.IsVisible.Should().BeTrue();
            harness.CaptionText.Text.Should().Be("Already in Tweaks");
            Body(harness).Classes.Contains(WindowDropFeedback.DropOverPseudoClass).Should().BeTrue();
            harness.WindowTarget.Inspected.Should().BeEmpty("the page handled the drag, so the window zone only hides its feedback");
        });
    }

    [Fact]
    public async Task ModsFromAnotherCollection_CaptionTheMove()
    {
        var (loadout, here, elsewhere) = await Seed();
        var mod = await SeedMod(loadout, "Moves in", elsewhere.Id);
        var harness = await Show(loadout, here);

        await OnUi(() => Raise(harness, RawDragEventType.DragEnter, Mods(loadout, mod)));

        await OnUi(() =>
        {
            harness.CaptionText.Text.Should().StartWith("Move 1 mod to Tweaks");
            Body(harness).Classes.Contains(WindowDropFeedback.DropOverPseudoClass).Should().BeTrue();
        });
    }

    [Fact]
    public async Task LeavingThePage_ClearsTheCaptionAndTheHighlight()
    {
        var (loadout, here, elsewhere) = await Seed();
        var mod = await SeedMod(loadout, "Moves in", elsewhere.Id);
        var harness = await Show(loadout, here);
        var data = Mods(loadout, mod);

        await OnUi(() =>
        {
            Raise(harness, RawDragEventType.DragEnter, data);
            Raise(harness, RawDragEventType.DragLeave, data);
        });

        await OnUi(() =>
        {
            harness.Caption.IsVisible.Should().BeFalse();
            Body(harness).Classes.Contains(WindowDropFeedback.DropOverPseudoClass).Should().BeFalse();
        });
    }

    [Fact]
    public async Task DroppingModsOnTheCollectionPage_MovesThemIn()
    {
        var (loadout, here, elsewhere) = await Seed();
        var mod = await SeedMod(loadout, "Moves in", elsewhere.Id);
        var harness = await Show(loadout, here);
        var data = Mods(loadout, mod);

        await OnUi(() =>
        {
            Raise(harness, RawDragEventType.DragEnter, data);
            Raise(harness, RawDragEventType.Drop, data);
        });

        await Eventually(() => LoadoutItem.Load(Connection.Db, mod).ParentId.Value.Should().Be(here.Id));
    }

    [Fact]
    public async Task OnTheAllPage_ModsFallThroughToTheWindow()
    {
        var (loadout, _, elsewhere) = await Seed();
        var mod = await SeedMod(loadout, "Stays put", elsewhere.Id);
        var harness = await Show(loadout, null);

        await OnUi(() => Raise(harness, RawDragEventType.DragEnter, Mods(loadout, mod)));

        await OnUi(() =>
        {
            harness.Caption.IsVisible.Should().BeFalse();
            Body(harness).Classes.Contains(WindowDropFeedback.DropOverPseudoClass).Should().BeFalse();
            harness.WindowTarget.Inspected.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LoadoutMods>();
        });
    }
}
