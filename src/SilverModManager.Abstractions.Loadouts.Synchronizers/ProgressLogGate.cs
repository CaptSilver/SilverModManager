using System.Diagnostics;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Lets a long, silent step log how far it has got without logging once per file: <see cref="TryTake"/> says yes at
/// most once per interval, whichever worker asks first.
/// </summary>
internal sealed class ProgressLogGate(TimeSpan interval)
{
    /// <summary>How often a long step writes its progress line.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private long _lastTaken = Stopwatch.GetTimestamp();

    public ProgressLogGate() : this(DefaultInterval) {}

    /// <summary>Whether a line is due now. The interval is counted from the gate's creation, so a step's first line is the one it logs at its start.</summary>
    public bool TryTake()
    {
        var last = Interlocked.Read(ref _lastTaken);
        if (Stopwatch.GetElapsedTime(last) < interval) return false;
        return Interlocked.CompareExchange(ref _lastTaken, Stopwatch.GetTimestamp(), last) == last;
    }
}
