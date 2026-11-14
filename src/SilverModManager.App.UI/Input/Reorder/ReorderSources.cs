using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>Reads the row a focused element belongs to; false when the focus is not on a row of this page.</summary>
public delegate bool RowReader<TKey>(IInputElement focused, [MaybeNullWhen(false)] out TKey key, [NotNullWhen(true)] out Control? row);

/// <summary>What a page offers the pad for picking up its rows: it knows how to find the row and its key from the focus.</summary>
public interface IReorderSource
{
    /// <summary>Whether the focus is on a row that can be picked up.</summary>
    bool CanBegin(IInputElement focused);

    /// <summary>Lifts the focused row, or returns null when the focus is not on one. Throws when the row is no longer in the order.</summary>
    ReorderLift? Begin(IInputElement focused);

    /// <summary>How many rows fit in the page the focus is on, which is how far the triggers move.</summary>
    int PageSize(IInputElement focused);
}

/// <summary>A row picked up from a page: the session that moves it and the element drawn lifted.</summary>
public sealed record ReorderLift(IReorderSession Session, Control Row, int Count);

/// <summary>A page's <see cref="IReorderSource"/> over one <see cref="IReorderable{TKey}"/>.</summary>
public sealed class ReorderSource<TKey>(IReorderable<TKey> target, RowReader<TKey> read, TimeProvider time) : IReorderSource where TKey : notnull
{
    private const int FallbackPageSize = 8;

    public bool CanBegin(IInputElement focused) => read(focused, out _, out _);

    public ReorderLift? Begin(IInputElement focused)
    {
        if (!read(focused, out var key, out var row)) return null;

        var session = new ReorderSession<TKey>(target, time);
        session.Start(key);
        return new ReorderLift(session, row, target.CountFor(key));
    }

    public int PageSize(IInputElement focused)
    {
        if (!read(focused, out _, out var row) || row.Bounds.Height <= 0) return FallbackPageSize;

        var viewer = FocusAncestors.Of(row).OfType<ScrollViewer>().FirstOrDefault();
        return viewer is null ? FallbackPageSize : Math.Max(1, (int)(viewer.Viewport.Height / row.Bounds.Height));
    }
}

/// <summary>The page-level hook the pad uses to find out whether, and how, the row it is on can be reordered.</summary>
public static class ReorderSources
{
    public static readonly AttachedProperty<IReorderSource?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Control, IReorderSource?>("Source", typeof(ReorderSources));

    public static IReorderSource? GetSource(Control control) => control.GetValue(SourceProperty);

    public static void SetSource(Control control, IReorderSource? value) => control.SetValue(SourceProperty, value);

    /// <summary>The source of the nearest page around the focus, or null when the focus is on nothing that reorders.</summary>
    internal static IReorderSource? For(IInputElement? focused) =>
        FocusAncestors.Of(focused).OfType<Control>().Select(GetSource).FirstOrDefault(source => source is not null);
}
