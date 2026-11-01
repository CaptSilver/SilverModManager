using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Controls.DragAndDrop;

/// <summary>The Avalonia drag protocol of <see cref="DropZone"/>, with a scripted target and a recording feedback.</summary>
[Collection(HeadlessUi.Collection)]
public class DropZoneTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly DragPayload.DroppedFiles Files = new(["/tmp/a.zip"]);

    private sealed class ScriptedTarget(Func<DragPayload, DropVerdict> verdict) : IDropTarget
    {
        public int Inspections { get; private set; }
        public List<DragPayload> Drops { get; } = [];
        public Func<DragPayload, Task>? OnDrop { get; set; }

        public DropVerdict Inspect(DragPayload payload)
        {
            Inspections++;
            return verdict(payload);
        }

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Drops.Add(payload);
            return OnDrop?.Invoke(payload) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingFeedback : IDropFeedback
    {
        public List<(Control Over, DropVerdict Verdict)> Shown { get; } = [];
        public int Hides { get; private set; }

        public void Show(Control over, DropVerdict verdict) => Shown.Add((over, verdict));

        public void Hide() => Hides++;
    }

    /// <summary>What the window saw once every handler below it had run.</summary>
    private sealed class Probe
    {
        public bool Handled { get; set; }
        public DragDropEffects Effects { get; set; }
    }

    private sealed record Harness(Window Window, Border Inner, Border Plain, Probe Probe);

    private static readonly DropVerdict Accept = new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 mod");

    private static Border Area() => new() { Background = Brushes.Gray, Width = 100, Height = 100 };

    private async Task<Harness> Show(Action<Window, Border> wire)
    {
        return await OnUi(() =>
        {
            var inner = Area();
            var plain = Area();
            var window = Track(new Window
            {
                Width = 300, Height = 200,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { inner, plain } },
            });

            wire(window, inner);

            // Registered after the zones, so on each element it runs after theirs; the window is last of all.
            var probe = new Probe();
            void Record(object? _, DragEventArgs e)
            {
                probe.Handled = e.Handled;
                probe.Effects = e.DragEffects;
            }
            foreach (var routed in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
                window.AddHandler(routed, Record, handledEventsToo: true);

            ShowRendered(window);
            return Task.FromResult(new Harness(window, inner, plain, probe));
        });
    }

    private static void Raise(Harness harness, Control over, RawDragEventType type, IDataObject data)
    {
        var centre = over.TranslatePoint(new Point(over.Bounds.Width / 2, over.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.DragDrop(centre, type, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private static IErrorPresenter Errors() => Substitute.For<IErrorPresenter>();

    [Fact]
    public async Task EnterOnAccept_SetsTheEffectHandlesTheEventAndShowsTheVerdict()
    {
        var feedback = new RecordingFeedback();
        var target = new ScriptedTarget(_ => new DropVerdict.Accept(DragDropEffects.Move, "Move 1 mod"));
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), feedback));

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, DragPayloads.ToDataObject(Files));

            harness.Probe.Handled.Should().BeTrue();
            harness.Probe.Effects.Should().Be(DragDropEffects.Move);
            feedback.Shown.Should().ContainSingle().Which.Should().Be((harness.Inner, new DropVerdict.Accept(DragDropEffects.Move, "Move 1 mod")));
        });
    }

    [Fact]
    public async Task EnterThenTwoOvers_OfTheSameDataObject_InspectOnce()
    {
        var target = new ScriptedTarget(_ => Accept);
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.DragOver, data);
            Raise(harness, harness.Inner, RawDragEventType.DragOver, data);

            target.Inspections.Should().Be(1);
        });
    }

    [Fact]
    public async Task AnEnterAfterADrop_InspectsAgain()
    {
        var target = new ScriptedTarget(_ => Accept);
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);
            var afterDrop = target.Inspections;

            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);

            target.Inspections.Should().Be(afterDrop + 1);
        });
    }

    [Fact]
    public async Task ANewDataObject_IsInspectedEvenWhenTheOldDragNeverEnded()
    {
        var target = new ScriptedTarget(_ => Accept);
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, DragPayloads.ToDataObject(Files));
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, DragPayloads.ToDataObject(Files));

            target.Inspections.Should().Be(2);
        });
    }

    [Fact]
    public async Task Refuse_SetsNoEffectHandlesTheEventAndShowsTheReason()
    {
        var feedback = new RecordingFeedback();
        var refusal = new DropVerdict.Refuse("That collection belongs to Nexus");
        var harness = await Show((_, inner) => new DropZone(inner, new ScriptedTarget(_ => refusal), Errors(), feedback));

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, DragPayloads.ToDataObject(Files));

            harness.Probe.Handled.Should().BeTrue();
            harness.Probe.Effects.Should().Be(DragDropEffects.None);
            feedback.Shown.Should().ContainSingle().Which.Verdict.Should().Be(refusal);
        });
    }

    [Fact]
    public async Task Ignore_SetsNoEffectLeavesTheEventUnhandledAndHidesTheFeedback()
    {
        var feedback = new RecordingFeedback();
        var harness = await Show((_, inner) => new DropZone(inner, new ScriptedTarget(_ => new DropVerdict.Ignore()), Errors(), feedback));

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, DragPayloads.ToDataObject(Files));

            harness.Probe.Handled.Should().BeFalse();
            harness.Probe.Effects.Should().Be(DragDropEffects.None);
            feedback.Shown.Should().BeEmpty();
            feedback.Hides.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task ADataObjectWithNoPayload_IsIgnoredWithoutAskingTheTarget()
    {
        var target = new ScriptedTarget(_ => Accept);
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, new DataObject());

            harness.Probe.Handled.Should().BeFalse();
            target.Inspections.Should().Be(0);
        });
    }

    [Fact]
    public async Task Leave_HidesTheFeedback()
    {
        var feedback = new RecordingFeedback();
        var harness = await Show((_, inner) => new DropZone(inner, new ScriptedTarget(_ => Accept), Errors(), feedback));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            var before = feedback.Hides;

            Raise(harness, harness.Inner, RawDragEventType.DragLeave, data);

            feedback.Hides.Should().Be(before + 1);
        });
    }

    [Fact]
    public async Task DropOnAccept_HandsThePayloadToTheTargetAndHidesTheFeedback()
    {
        var feedback = new RecordingFeedback();
        var target = new ScriptedTarget(_ => Accept);
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), feedback));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            var before = feedback.Hides;
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);

            target.Drops.Should().ContainSingle().Which.Should().BeSameAs(Files);
            harness.Probe.Handled.Should().BeTrue();
            feedback.Hides.Should().BeGreaterThan(before);
        });
    }

    [Fact]
    public async Task DropOnRefuse_DoesNotCallTheTarget()
    {
        var target = new ScriptedTarget(_ => new DropVerdict.Refuse("No"));
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);

            target.Drops.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task DropOnIgnore_DoesNotCallTheTargetAndLeavesTheEventUnhandled()
    {
        var target = new ScriptedTarget(_ => new DropVerdict.Ignore());
        var harness = await Show((_, inner) => new DropZone(inner, target, Errors(), new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);

            target.Drops.Should().BeEmpty();
            harness.Probe.Handled.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AnOuterZone_OverAnInnerZoneThatHandledTheEvent_ShowsNothingAndGetsNoDrop()
    {
        var outerFeedback = new RecordingFeedback();
        var innerFeedback = new RecordingFeedback();
        var outerTarget = new ScriptedTarget(_ => Accept);
        var innerTarget = new ScriptedTarget(_ => Accept);
        var harness = await Show((window, inner) =>
        {
            _ = new DropZone(inner, innerTarget, Errors(), innerFeedback);
            _ = new DropZone(window, outerTarget, Errors(), outerFeedback, outer: true);
        });
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);

            outerFeedback.Shown.Should().BeEmpty();
            outerTarget.Drops.Should().BeEmpty();
            outerTarget.Inspections.Should().Be(0);
            innerTarget.Drops.Should().ContainSingle();
        });
    }

    [Fact]
    public async Task AnOuterZone_HidesItsOwnFeedback_WhenAnInnerZoneTakesOverTheDrag()
    {
        var outerFeedback = new RecordingFeedback();
        var harness = await Show((window, inner) =>
        {
            _ = new DropZone(inner, new ScriptedTarget(_ => Accept), Errors(), new RecordingFeedback());
            _ = new DropZone(window, new ScriptedTarget(_ => Accept), Errors(), outerFeedback, outer: true);
        });
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Plain, RawDragEventType.DragEnter, data);
            outerFeedback.Shown.Should().ContainSingle();
            var hides = outerFeedback.Hides;

            Raise(harness, harness.Inner, RawDragEventType.DragOver, data);

            outerFeedback.Hides.Should().Be(hides + 1);
            outerFeedback.Shown.Should().ContainSingle();
        });
    }

    [Fact]
    public async Task AnOuterZone_TakesTheDrag_WhenTheInnerZoneIgnoresIt()
    {
        var outerTarget = new ScriptedTarget(_ => Accept);
        var harness = await Show((window, inner) =>
        {
            _ = new DropZone(inner, new ScriptedTarget(_ => new DropVerdict.Ignore()), Errors(), new RecordingFeedback());
            _ = new DropZone(window, outerTarget, Errors(), new RecordingFeedback(), outer: true);
        });
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);

            outerTarget.Drops.Should().ContainSingle();
        });
    }

    [Fact]
    public async Task ADropThatThrows_IsReportedAndNothingEscapes()
    {
        var errors = Errors();
        var failure = new InvalidOperationException("boom");
        var target = new ScriptedTarget(_ => Accept) { OnDrop = _ => Task.FromException(failure) };
        var harness = await Show((_, inner) => new DropZone(inner, target, errors, new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);
        });

        await errors.Received(1).Report(failure, "handling the drop");
    }

    [Fact]
    public async Task ADropThatThrowsSynchronously_IsReportedToo()
    {
        var errors = Errors();
        var failure = new InvalidOperationException("boom");
        var target = new ScriptedTarget(_ => Accept) { OnDrop = _ => throw failure };
        var harness = await Show((_, inner) => new DropZone(inner, target, errors, new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);
            Raise(harness, harness.Inner, RawDragEventType.Drop, data);
        });

        await errors.Received(1).Report(failure, "handling the drop");
    }

    [Fact]
    public async Task ADisposedZone_NoLongerHandlesDrags()
    {
        var target = new ScriptedTarget(_ => Accept);
        DropZone? zone = null;
        var harness = await Show((_, inner) => zone = new DropZone(inner, target, Errors(), new RecordingFeedback()));
        var data = DragPayloads.ToDataObject(Files);

        await OnUi(() =>
        {
            zone!.Dispose();
            Raise(harness, harness.Inner, RawDragEventType.DragEnter, data);

            harness.Probe.Handled.Should().BeFalse();
            target.Inspections.Should().Be(0);
        });
    }
}
