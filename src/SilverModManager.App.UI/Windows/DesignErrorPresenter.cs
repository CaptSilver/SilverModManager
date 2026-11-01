namespace SilverModManager.App.UI.Windows;

/// <summary>For design-time view models, which have no window to show an error in.</summary>
public sealed class DesignErrorPresenter : IErrorPresenter
{
    public Task Report(Exception exception, string activity) => Task.CompletedTask;

    public void Connect(IObservable<SilverModManager.Abstractions.Logging.LogMessage> errors) { }
}
