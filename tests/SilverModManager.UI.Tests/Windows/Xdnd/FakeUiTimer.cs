using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// An IUiTimer that never runs on its own: each After is recorded as a <see cref="Handle"/>, and a test fires one
/// by its delay. A disposed or already fired handle never fires.
/// </summary>
public sealed class FakeUiTimer : IUiTimer
{
    public List<Handle> Armed { get; } = [];

    public IDisposable After(TimeSpan delay, Action callback)
    {
        var handle = new Handle(delay, callback);
        Armed.Add(handle);
        return handle;
    }

    /// <summary>Handles that are neither disposed nor fired.</summary>
    public IReadOnlyList<Handle> Live => Armed.Where(h => h is { Disposed: false, Fired: false }).ToList();

    /// <summary>Runs the oldest live handle armed with <paramref name="delay"/>; throws when there is none, since a test that fires nothing proves nothing.</summary>
    public void Fire(TimeSpan delay)
    {
        var handle = Live.FirstOrDefault(h => h.Delay == delay)
            ?? throw new InvalidOperationException($"No live timer armed for {delay}");
        handle.Fired = true;
        handle.Callback();
    }

    public sealed class Handle(TimeSpan delay, Action callback) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public Action Callback { get; } = callback;
        public bool Disposed { get; private set; }
        public bool Fired { get; set; }

        public void Dispose() => Disposed = true;
    }
}
