using System.Diagnostics;

namespace SilverModManager.UI.Sdk;

/// <summary>
/// Names what the UI thread is running right now, so a stall report can say what it was busy with.
/// </summary>
/// <remarks>
/// One static reference rather than a <c>[ThreadStatic]</c>: the UI thread writes it and the watchdog
/// thread reads it, and there is only one UI thread. In an async body the value stays set across awaits,
/// so readers must look at the age; a scope that finished its synchronous part long ago is not the culprit.
/// </remarks>
public static class UiOperation
{
    private sealed record Entry(string Name, long StartedAt, Entry? Outer);

    private static volatile Entry? _current;

    /// <summary>The innermost operation entered on the UI thread and the <see cref="Stopwatch"/> timestamp it started at.</summary>
    public static (string Name, long StartedAt)? Current
    {
        get
        {
            var entry = _current;
            return entry is null ? null : (entry.Name, entry.StartedAt);
        }
    }

    /// <summary>Marks <paramref name="name"/> as running until the returned scope is disposed.</summary>
    public static IDisposable Enter(string name)
    {
        var entry = new Entry(name, Stopwatch.GetTimestamp(), _current);
        _current = entry;
        return new Scope(entry);
    }

    private sealed class Scope(Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Only pop when this scope is still the innermost one; an out-of-order dispose must not
            // resurrect an operation that already ended.
            if (ReferenceEquals(_current, entry)) _current = entry.Outer;
        }
    }
}
