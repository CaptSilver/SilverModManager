using Avalonia.Threading;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>An <see cref="IUiTimer"/> over <see cref="DispatcherTimer"/>, stopped by its first tick so each delay fires once. Call <see cref="After"/> on the UI thread.</summary>
public sealed class DispatcherUiTimer : IUiTimer
{
    public IDisposable After(TimeSpan delay, Action callback)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            callback();
        };
        timer.Start();
        return new Handle(timer);
    }

    private sealed class Handle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
