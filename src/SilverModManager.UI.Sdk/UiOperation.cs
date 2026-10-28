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
    private sealed record Entry(string Name, long StartedAt, Entry? Outer)
    {
        public volatile bool Ended;
    }

    private static volatile Entry? _current;

    /// <summary>The innermost operation entered on the UI thread and the <see cref="Stopwatch"/> timestamp it started at.</summary>
    public static (string Name, long StartedAt)? Current
    {
        get
        {
            // Skip scopes that ended out of order, so interleaved async scopes cannot bring a finished operation back.
            var entry = _current;
            while (entry is { Ended: true }) entry = entry.Outer;
            return entry is null ? null : (entry.Name, entry.StartedAt);
        }
    }

    /// <summary>How many entries hang off the current one, ended or not; the chain must not grow with the number of scopes.</summary>
    internal static int ChainLength
    {
        get
        {
            var length = 0;
            for (var entry = _current; entry is not null; entry = entry.Outer) length++;
            return length;
        }
    }

    // An outer that ended out of order is no use to anyone, so the chain never links to one.
    private static Entry? SkipEnded(Entry? entry)
    {
        while (entry is { Ended: true }) entry = entry.Outer;
        return entry;
    }

    /// <summary>Marks <paramref name="name"/> as running until the returned scope is disposed.</summary>
    public static IDisposable Enter(string name)
    {
        var entry = new Entry(name, Stopwatch.GetTimestamp(), SkipEnded(_current));
        _current = entry;
        return new Scope(entry);
    }

    private sealed class Scope(Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Ended = true;
            if (ReferenceEquals(_current, entry)) _current = SkipEnded(entry.Outer);
        }
    }
}
