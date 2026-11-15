using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Controls;

/// <summary>The timings, transforms and hit-testing every reorder drag shares.</summary>
internal static class ReorderDragVisuals
{
    public const double LiftedOpacity = 0.92;
    public static readonly TimeSpan EdgeScrollInterval = TimeSpan.FromMilliseconds(16);

    private static readonly TimeSpan DefaultSlideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultLiftDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>The theme's <c>SpineReorderSlideDuration</c> as seen from <paramref name="owner"/>, else 150 ms.</summary>
    public static TimeSpan SlideDuration(StyledElement owner) => ThemeDuration(owner, "SpineReorderSlideDuration", DefaultSlideDuration);

    /// <summary>The theme's <c>SpineReorderLiftDuration</c> as seen from <paramref name="owner"/>, else 120 ms.</summary>
    public static TimeSpan LiftDuration(StyledElement owner) => ThemeDuration(owner, "SpineReorderLiftDuration", DefaultLiftDuration);

    public static Transitions SlideTransitions(TimeSpan duration) =>
    [
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = duration,
            Easing = new CubicEaseOut(),
        },
    ];

    /// <summary>Always translate then scale, so a transition can interpolate between any two. Vertical callers pass (0, y).</summary>
    public static ITransform Transform(Vector offset, double scale)
    {
        var builder = TransformOperations.CreateBuilder(2);
        builder.AppendTranslate(offset.X, offset.Y);
        builder.AppendScale(scale, scale);
        return builder.Build();
    }

    /// <summary>Waits, then posts the action to the UI thread. Callers discard the task: nothing needs to await it.</summary>
    public static async Task DelayThen(TimeSpan delay, Action action)
    {
        await Task.Delay(delay);
        Dispatcher.UIThread.Post(action);
    }

    /// <summary>Index in <paramref name="children"/> of the child that <paramref name="source"/> is or sits inside; -1 when none.</summary>
    public static int IndexOfChildUnder(IReadOnlyList<Control> children, object? source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (ReferenceEquals(children[i], visual)) return i;
            }
        }
        return -1;
    }

    private static TimeSpan ThemeDuration(StyledElement owner, string key, TimeSpan fallback) =>
        owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is TimeSpan duration ? duration : fallback;
}
