using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.App.UI.Controls;

namespace SilverModManager.UI.Tests.Controls;

public class ReorderDragVisualsTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task Transform_IsTranslateThenScale()
    {
        await OnUi(() =>
        {
            var transform = ReorderDragVisuals.Transform(new Vector(3, 7), 1.5);

            var operations = transform.Should().BeOfType<TransformOperations>().Subject.Operations;
            operations.Select(operation => operation.Type).Should().Equal(TransformOperation.OperationType.Translate, TransformOperation.OperationType.Scale);
            operations[0].Data.Translate.X.Should().Be(3);
            operations[0].Data.Translate.Y.Should().Be(7);
            operations[1].Data.Scale.X.Should().Be(1.5);
        });
    }

    [Fact]
    public async Task IndexOfChildUnder_FindsTheChildThatHoldsTheSource()
    {
        await OnUi(() =>
        {
            var inner = new Border();
            var first = new Border();
            var second = new Border { Child = new Border { Child = inner } };
            Control[] children = [first, second];

            ReorderDragVisuals.IndexOfChildUnder(children, inner).Should().Be(1);
            ReorderDragVisuals.IndexOfChildUnder(children, first).Should().Be(0);
        });
    }

    [Fact]
    public async Task IndexOfChildUnder_OutsideEveryChild_IsMinusOne()
    {
        await OnUi(() =>
        {
            Control[] children = [new Border(), new Border()];

            ReorderDragVisuals.IndexOfChildUnder(children, new Border()).Should().Be(-1);
            ReorderDragVisuals.IndexOfChildUnder(children, null).Should().Be(-1);
            ReorderDragVisuals.IndexOfChildUnder(children, "not a visual").Should().Be(-1);
        });
    }

    [Fact]
    public async Task SlideDuration_PrefersTheThemeResource_ElseTheDefault()
    {
        await OnUi(() =>
        {
            var plain = new Border();
            ReorderDragVisuals.SlideDuration(plain).Should().Be(TimeSpan.FromMilliseconds(150));
            ReorderDragVisuals.LiftDuration(plain).Should().Be(TimeSpan.FromMilliseconds(120));

            var themed = new Border();
            themed.Resources["SpineReorderSlideDuration"] = TimeSpan.FromMilliseconds(33);
            themed.Resources["SpineReorderLiftDuration"] = TimeSpan.FromMilliseconds(44);
            ReorderDragVisuals.SlideDuration(themed).Should().Be(TimeSpan.FromMilliseconds(33));
            ReorderDragVisuals.LiftDuration(themed).Should().Be(TimeSpan.FromMilliseconds(44));
        });
    }

    [Fact]
    public async Task DelayThen_RunsTheActionOnTheUIThreadAfterTheDelay()
    {
        var ranOnUi = new TaskCompletionSource<bool>();

        var task = await OnUi(() => Task.FromResult(ReorderDragVisuals.DelayThen(TimeSpan.FromMilliseconds(20), () => ranOnUi.TrySetResult(Dispatcher.UIThread.CheckAccess()))));

        await task;
        (await ranOnUi.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
    }

    [Fact]
    public async Task SlideTransitions_AnimateTheRenderTransformOverTheGivenDuration()
    {
        await OnUi(() =>
        {
            var transitions = ReorderDragVisuals.SlideTransitions(TimeSpan.FromMilliseconds(77));

            var transition = transitions.Should().ContainSingle().Which.Should().BeOfType<Avalonia.Animation.TransformOperationsTransition>().Subject;
            transition.Property.Should().Be(Visual.RenderTransformProperty);
            transition.Duration.Should().Be(TimeSpan.FromMilliseconds(77));
        });
    }
}
