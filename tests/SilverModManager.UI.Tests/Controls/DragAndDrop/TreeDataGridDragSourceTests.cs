using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Controls.DragAndDrop;

/// <summary>
/// A drag that starts on a row of a real <see cref="TreeDataGrid"/>: which rows it carries, when it starts, and that it
/// runs through Avalonia's in-process drag source (the headless platform binds none, as the desktop doesn't) to a
/// <see cref="DropZone"/> without disturbing the selection.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class TreeDataGridDragSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Row(string Name);

    private sealed record Rows(IReadOnlyList<Row> Models) : DragPayload;

    private sealed class RecordingTarget : IDropTarget
    {
        public List<DragPayload> Dropped { get; } = [];
        public DropVerdict Inspect(DragPayload payload) => new DropVerdict.Accept(DragDropEffects.Copy, "Drop here");

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Dropped.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public required Window Window { get; init; }
        public required Avalonia.Controls.TreeDataGrid Grid { get; init; }
        public required FlatTreeDataGridSource<Row> Source { get; init; }
        public required Border Target { get; init; }
        public required RecordingTarget Dropped { get; init; }
        public required List<IReadOnlyList<Row>> Asked { get; init; }
        public required Row[] Rows { get; init; }
        public TreeDataGridDragSource<Row> DragSource { get; set; } = null!;
    }

    private async Task<Harness> Show(Func<IReadOnlyList<Row>, DragPayload?>? payloadFor = null)
    {
        return await OnUi(async () =>
        {
            var rows = new[] { new Row("One"), new Row("Two"), new Row("Three"), new Row("Four") };
            var source = new FlatTreeDataGridSource<Row>(rows)
            {
                Columns = { new TextColumn<Row, string>("Name", row => row.Name) },
            };
            source.RowSelection!.SingleSelect = false;
            var grid = new Avalonia.Controls.TreeDataGrid { Source = source, Width = 300 };
            var target = new Border { Width = 200, Background = Avalonia.Media.Brushes.Gray };
            var host = new Grid { ColumnDefinitions = { new ColumnDefinition(300, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) } };
            Grid.SetColumn(target, 1);
            host.Children.Add(grid);
            host.Children.Add(target);
            var window = Track(new Window { Width = 600, Height = 400, Content = host });
            var recording = new RecordingTarget();
            _ = new DropZone(target, recording, Substitute.For<IErrorPresenter>());
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);

            var asked = new List<IReadOnlyList<Row>>();
            var harness = new Harness { Window = window, Grid = grid, Source = source, Target = target, Dropped = recording, Asked = asked, Rows = rows };
            harness.DragSource = new TreeDataGridDragSource<Row>(
                grid,
                () => source.RowSelection.SelectedItems.OfType<Row>().ToArray(),
                models =>
                {
                    asked.Add(models);
                    return payloadFor is null ? new Rows(models) : payloadFor(models);
                },
                DragDropEffects.Copy);
            return harness;
        });
    }

    private static Control RowControl(Harness harness, int index) =>
        harness.Grid.GetVisualDescendants().OfType<TreeDataGridRow>().Single(row => ReferenceEquals(row.Model, harness.Rows[index]));

    private static Point CentreOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    // The moves carry the held button, as a real pointer's do: the in-process drag source reads the button from them.
    private static async Task Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        const int steps = 8;
        for (var i = 1; i <= steps; i++)
        {
            window.MouseMove(from + (to - from) * i / steps, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void RowsToDrag_IsTheSelectionWhenThePressedRowIsInIt()
    {
        var rows = new[] { new Row("a"), new Row("b"), new Row("c") };

        TreeDataGridDragSource<Row>.RowsToDrag(rows[1], rows).Should().Equal(rows);
    }

    [Fact]
    public void RowsToDrag_IsThePressedRowAloneWhenItIsOutsideTheSelection()
    {
        var rows = new[] { new Row("a"), new Row("b"), new Row("c") };

        TreeDataGridDragSource<Row>.RowsToDrag(rows[2], [rows[0], rows[1]]).Should().Equal(rows[2]);
    }

    [Fact]
    public async Task PressingOneOfThreeSelectedRows_AsksForAllThree()
    {
        var harness = await Show();
        await OnUi(() =>
        {
            harness.Source.RowSelection!.Select(0);
            harness.Source.RowSelection.Select(1);
            harness.Source.RowSelection.Select(2);
        });
        var from = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 1), harness.Window)));

        await OnUi(() => Drag(harness.Window, from, from + new Point(0, 30)));

        harness.Asked.Should().ContainSingle().Which.Should().Equal(harness.Rows[0], harness.Rows[1], harness.Rows[2]);
    }

    [Fact]
    public async Task PressingAnUnselectedRow_AsksForThatRowAlone()
    {
        var harness = await Show();
        await OnUi(() => harness.Source.RowSelection!.Select(0));
        var from = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 3), harness.Window)));

        await OnUi(() => Drag(harness.Window, from, from + new Point(0, -30)));

        harness.Asked.Should().ContainSingle().Which.Should().Equal(harness.Rows[3]);
    }

    [Fact]
    public async Task AClickWithoutMovement_StartsNoDrag()
    {
        var harness = await Show();
        var at = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 1), harness.Window)));

        await OnUi(() => Drag(harness.Window, at, at + new Point(1, 1)));

        harness.Asked.Should().BeEmpty();
        harness.Dropped.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenThereIsNothingToDrag_NoDragStarts()
    {
        var harness = await Show(payloadFor: _ => null);
        var from = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 1), harness.Window)));
        var onTarget = await OnUi(() => Task.FromResult(CentreOf(harness.Target, harness.Window)));

        await OnUi(() => Drag(harness.Window, from, onTarget));

        harness.Asked.Should().ContainSingle();
        harness.Dropped.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task DraggingThreeSelectedRowsOntoAZone_DropsThemOnceAndKeepsTheSelection()
    {
        var harness = await Show();
        await OnUi(() =>
        {
            harness.Source.RowSelection!.Select(0);
            harness.Source.RowSelection.Select(1);
            harness.Source.RowSelection.Select(2);
        });
        var from = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 2), harness.Window)));
        var onTarget = await OnUi(() => Task.FromResult(CentreOf(harness.Target, harness.Window)));

        await OnUi(() => Drag(harness.Window, from, onTarget));

        harness.Dropped.Dropped.Should().ContainSingle().Which.Should().BeOfType<Rows>()
            .Which.Models.Should().Equal(harness.Rows[0], harness.Rows[1], harness.Rows[2]);
        await OnUi(() => harness.Source.RowSelection!.SelectedItems.Should().HaveCount(3));
    }

    [Fact]
    public async Task AfterDispose_APressAndMoveStartsNothing()
    {
        var harness = await Show();
        var from = await OnUi(() => Task.FromResult(CentreOf(RowControl(harness, 1), harness.Window)));
        await OnUi(() => harness.DragSource.Dispose());

        await OnUi(() => Drag(harness.Window, from, from + new Point(0, 30)));

        harness.Asked.Should().BeEmpty();
    }
}
