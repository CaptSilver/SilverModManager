namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Works out how long the current phase of an install has left from how fast it has been going over the last few
/// minutes. It says nothing until it has a full minute of the phase to go on: a rate taken from the first seconds is a
/// wild guess, and a wrong ETA is worse than none.
/// </summary>
public sealed class InstallEstimator
{
    /// <summary>The least stretch of a phase the rate may be taken from.</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromMinutes(1);

    /// <summary>How far back the rate looks, so a slow start does not hold the estimate down for the rest of the phase.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SampleSpacing = TimeSpan.FromSeconds(1);

    private readonly List<Sample> _samples = [];
    private InstallPhase? _phase;

    private readonly record struct Sample(DateTimeOffset At, int Items, long Bytes);

    /// <summary>
    /// Records <paramref name="snapshot"/>, seen at <paramref name="now"/>, and returns the time the phase has left, or
    /// null while there is too little data, no progress, or nothing to count (a phase without items).
    /// </summary>
    public TimeSpan? Add(DateTimeOffset now, ModlistInstallProgress.Snapshot snapshot)
    {
        if (_phase != snapshot.Phase)
        {
            _phase = snapshot.Phase;
            _samples.Clear();
        }

        var latest = new Sample(now, snapshot.DoneItems, snapshot.DoneBytes);
        if (_samples.Count == 0 || now - _samples[^1].At >= SampleSpacing) _samples.Add(latest);
        else _samples[^1] = latest;

        while (_samples.Count > 2 && now - _samples[1].At >= Window) _samples.RemoveAt(0);

        if (snapshot.TotalItems <= 0) return null;
        var first = _samples[0];
        var elapsed = (now - first.At).TotalSeconds;
        if (elapsed < MinimumData.TotalSeconds) return null;

        // Bytes follow the work more evenly than items when a phase has them; one huge file is not one small one.
        var (done, total, rate) = snapshot.TotalBytes > 0 && snapshot.DoneBytes > first.Bytes
            ? (snapshot.DoneBytes, snapshot.TotalBytes, (snapshot.DoneBytes - first.Bytes) / elapsed)
            : (snapshot.DoneItems, snapshot.TotalItems, (snapshot.DoneItems - first.Items) / elapsed);
        if (rate <= 0) return null;

        return TimeSpan.FromSeconds(Math.Max(0, total - done) / rate);
    }
}
