using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// A fake clock that remembers how long each timer created on it was set for, so a test can tell that the job is
/// waiting (a timer of that length exists) or that it never waited (none does) without sleeping in real time.
/// </summary>
internal sealed class RecordingClock : FakeTimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _dueTimes = new();

    public IReadOnlyCollection<TimeSpan> DueTimes => _dueTimes;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _dueTimes.Enqueue(dueTime);
        return base.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Yields until a timer of exactly <paramref name="dueTime"/> has been created.</summary>
    public async Task WaitForTimer(TimeSpan dueTime)
    {
        while (!_dueTimes.Contains(dueTime)) await Task.Yield();
    }
}
