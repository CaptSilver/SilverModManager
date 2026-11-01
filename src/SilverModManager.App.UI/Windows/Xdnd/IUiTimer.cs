namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>A one-shot delay whose callback runs on the UI thread. Dispose cancels it.</summary>
public interface IUiTimer
{
    IDisposable After(TimeSpan delay, Action callback);
}
