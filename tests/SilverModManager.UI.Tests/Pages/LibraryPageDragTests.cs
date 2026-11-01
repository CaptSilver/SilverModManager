using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A row of the Library page can be picked up and dropped on a target elsewhere in the window.</summary>
[Collection(HeadlessUi.Collection)]
public class LibraryPageDragTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class RecordingTarget : IDropTarget
    {
        public List<DragPayload> Dropped { get; } = [];
        public DropVerdict Inspect(DragPayload payload) => new DropVerdict.Accept(DragDropEffects.Copy, "Install here");

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Dropped.Add(payload);
            return Task.CompletedTask;
        }
    }

    /// <summary>The collection's real target decides what is acceptable; the drop is recorded rather than installed.</summary>
    private sealed class RecordingCollectionTarget(RecordingTarget recorder, CollectionDropTarget real) : IDropTarget
    {
        public DropVerdict Inspect(DragPayload payload) => real.Inspect(payload);
        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken) => recorder.DropAsync(payload, cancellationToken);
    }

    private async Task<LibraryItemId> SeedLocalFile(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = Hash.From((ulong)Guid.NewGuid().GetHashCode()),
            Size = NexusMods.Paths.Size.From(1024),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { OriginalPath = "/mods/" + name, LibraryFile = file };
        LibraryDestinations.Write(tx, id, new LoadoutAcquisition(loadout.LoadoutId).Destination(Connection.Db));
        var result = await tx.Commit();
        return LibraryItemId.From(result[id]);
    }

    private sealed record Harness(Window Window, LibraryView View, Control Drop, RecordingTarget Target, Loadout.ReadOnly Loadout);

    /// <summary>
    /// Hosts the page next to what the row is dropped on: by default a bare zone, or whatever
    /// <paramref name="dropSide"/> builds around the recording <paramref name="target"/>.
    /// </summary>
    private async Task<Harness> Show(Loadout.ReadOnly loadout, Func<RecordingTarget, Control>? dropSide = null)
    {
        var target = new RecordingTarget();
        return await OnUi(async () =>
        {
            var vm = new LibraryViewModel(
                Substitute.For<IWindowManager>(),
                Provider,
                Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
                loadout.LoadoutId);
            var view = new LibraryView { ViewModel = vm };
            Control drop;
            if (dropSide is null)
            {
                var zone = new Border { Width = 200, Background = Avalonia.Media.Brushes.Gray };
                _ = new DropZone(zone, target, Substitute.For<IErrorPresenter>());
                drop = zone;
            }
            else
            {
                drop = dropSide(target);
            }
            Grid.SetColumn(drop, 1);
            var host = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(200, GridUnitType.Pixel) }, Children = { view, drop } };
            var window = Track(new Window { Width = 1200, Height = 800, Content = host });
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);
            return new Harness(window, view, drop, target, loadout);
        });
    }

    private async Task<Control> RowOf(Harness harness, LibraryItemId item)
    {
        Control? row = null;
        await EventuallyOnUi(() =>
        {
            Render(harness.Window);
            row = harness.View.GetVisualDescendants().OfType<TreeDataGridRow>()
                .FirstOrDefault(candidate => candidate.Model is CompositeItemModel<EntityId> model
                    && model.Get<LibraryComponents.LibraryItemIds>(LibraryColumns.Actions.LibraryItemIdsComponentKey).ItemIds.Contains(item));
            row.Should().NotBeNull("the seeded file shows as a row");
        });
        return row!;
    }

    private static Point PointIn(Control control, Window window, double x, double y) => control.TranslatePoint(new Point(x, y), window)!.Value;

    private static async Task DragToTarget(Harness harness, Control row)
    {
        var window = harness.Window;
        var from = PointIn(row, window, row.Bounds.Width / 3, row.Bounds.Height / 2);
        var to = PointIn(harness.Drop, window, harness.Drop.Bounds.Width / 2, harness.Drop.Bounds.Height / 2);
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 8; step++)
        {
            window.MouseMove(from + (to - from) * step / 8, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task DraggingALibraryRow_CarriesItsItemForThisLoadout()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Library page drag " + Guid.NewGuid());
        var wanted = await SeedLocalFile(loadout, "wanted-" + Guid.NewGuid().ToString("N") + ".zip");
        var harness = await Show(loadout);
        var row = await RowOf(harness, wanted);

        await OnUi(() => DragToTarget(harness, row));

        harness.Target.Dropped.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LibraryItems>()
            .Which.Should().BeEquivalentTo(new { Loadout = loadout.LoadoutId, Items = new[] { wanted } });
    }

    /// <summary>
    /// The page's source reads the adapter's selection, which the grid fills as rows are clicked, so this goes through
    /// real clicks: all three selected rows must be in it when the drag starts, and still be selected after the drop.
    /// </summary>
    [Fact]
    public async Task DraggingOneOfThreeSelectedRows_CarriesAllThreeAndKeepsThemSelected()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Library page multi drag " + Guid.NewGuid());
        var suffix = Guid.NewGuid().ToString("N");
        var items = new[]
        {
            await SeedLocalFile(loadout, $"a-{suffix}.zip"),
            await SeedLocalFile(loadout, $"b-{suffix}.zip"),
            await SeedLocalFile(loadout, $"c-{suffix}.zip"),
        };
        var harness = await Show(loadout);
        var rows = new List<Control>();
        foreach (var item in items) rows.Add(await RowOf(harness, item));

        await OnUi(() =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var at = PointIn(rows[i], harness.Window, rows[i].Bounds.Width / 3, rows[i].Bounds.Height / 2);
                var modifiers = i == 0 ? RawInputModifiers.None : RawInputModifiers.Control;
                harness.Window.MouseDown(at, MouseButton.Left, modifiers);
                harness.Window.MouseUp(at, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
            }
        });
        await EventuallyOnUi(() => ((ILibraryViewModel)harness.View.ViewModel!).Adapter.SelectedModels.Should().HaveCount(3));

        await OnUi(() => DragToTarget(harness, rows[1]));

        harness.Target.Dropped.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LibraryItems>()
            .Which.Items.Should().BeEquivalentTo(items);
        await OnUi(() => ((ILibraryViewModel)harness.View.ViewModel!).Adapter.SelectedModels.Should().HaveCount(3, "the drop is not a click, so it leaves the selection alone"));
    }

    /// <summary>
    /// The whole path the feature exists for: three selected rows go through the grid's drag source, over a real menu
    /// entry's drop zone, and reach the target of a user-owned collection as one payload.
    /// </summary>
    [Fact]
    public async Task DraggingThreeSelectedRowsOntoACollectionMenuEntry_DropsThemOnceAndKeepsThemSelected()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Library menu drag " + Guid.NewGuid());
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Menu drop");
        var suffix = Guid.NewGuid().ToString("N");
        var items = new[]
        {
            await SeedLocalFile(loadout, $"a-{suffix}.zip"),
            await SeedLocalFile(loadout, $"b-{suffix}.zip"),
            await SeedLocalFile(loadout, $"c-{suffix}.zip"),
        };
        var harness = await Show(loadout, recorder => new LeftMenuItemView
        {
            Width = 200,
            Height = 40,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            ViewModel = new LeftMenuItemViewModel(Substitute.For<IWorkspaceController>(), WorkspaceId.From(Guid.NewGuid()), null)
            {
                Text = new StringComponent("Menu drop"),
                DropTarget = new RecordingCollectionTarget(recorder, CollectionDropTarget.For(Provider, CollectionGroupId.From(group.Id))),
                ErrorPresenter = Substitute.For<IErrorPresenter>(),
            },
        });
        var rows = new List<Control>();
        foreach (var item in items) rows.Add(await RowOf(harness, item));

        await OnUi(() =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var at = PointIn(rows[i], harness.Window, rows[i].Bounds.Width / 3, rows[i].Bounds.Height / 2);
                var modifiers = i == 0 ? RawInputModifiers.None : RawInputModifiers.Control;
                harness.Window.MouseDown(at, MouseButton.Left, modifiers);
                harness.Window.MouseUp(at, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
            }
        });
        await EventuallyOnUi(() => ((ILibraryViewModel)harness.View.ViewModel!).Adapter.SelectedModels.Should().HaveCount(3));

        await OnUi(() => DragToTarget(harness, rows[1]));

        harness.Target.Dropped.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LibraryItems>()
            .Which.Items.Should().BeEquivalentTo(items);
        await OnUi(() => ((ILibraryViewModel)harness.View.ViewModel!).Adapter.SelectedModels.Should().HaveCount(3, "the drop is not a click, so it leaves the selection alone"));
    }
}
