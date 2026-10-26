using System.Reactive.Linq;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Logging;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Turns logged errors into the "Unhandled Exception" dialog, except the refusals a flow already explains
/// to the player in its own dialog.
/// </summary>
public static class UnhandledExceptionReporter
{
    /// <summary>
    /// Calls <paramref name="show"/> with a title, description and optional markdown details for each
    /// logged error that is not an expected refusal.
    /// </summary>
    public static IDisposable Connect(IObservable<LogMessage> errors, Action<string, string, string?> show)
    {
        return errors
            .Where(msg => !IsExpectedRefusal(msg.Exception))
            .Subscribe(msg =>
            {
                string? details = null;
                if (msg.Exception != null)
                {
                    details = $"""
                               ```
                               {msg.Exception}
                               ``` 
                               """;
                }

                show("Unhandled Exception", msg.Message, details);
            });
    }

    /// <summary>
    /// A <see cref="SynchronizerPreflightException"/> is raised before anything is written and is shown to the
    /// player by the apply flow. The job system logs the failed job with that exception, possibly wrapped in
    /// <see cref="AggregateException"/>s, so every leaf of the wrapping has to be a refusal for the error to be
    /// skipped; one genuine failure next to a refusal still counts as unhandled.
    /// </summary>
    public static bool IsExpectedRefusal(Exception? exception) => exception switch
    {
        null => false,
        SynchronizerPreflightException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsExpectedRefusal),
        _ => false,
    };
}
