using JetBrains.Annotations;
using R3;

namespace SilverModManager.Sdk.Settings;

/// <summary>
/// A settings row that is not a value to edit but a status line with one action beside it, such as signing in to
/// an account. The row still sits on a setting property (the page builds its rows from them); the property's
/// value is never changed from the row.
/// </summary>
[PublicAPI]
public class ActionButtonContainerOptions : IContainerOptions
{
    /// <summary>Builds the live source of the row's text and action once the page is built.</summary>
    public required Func<IServiceProvider, IActionButtonSource> CreateSource { get; init; }
}

/// <summary>What the row says now, and the label of its button; null when it offers no button.</summary>
[PublicAPI]
public readonly record struct ActionButtonState(string Text, string? ButtonLabel);

/// <summary>The text and the action behind an <see cref="ActionButtonContainerOptions"/> row.</summary>
[PublicAPI]
public interface IActionButtonSource
{
    ActionButtonState Current { get; }

    /// <summary>Starts with <see cref="Current"/> and follows every change after it.</summary>
    Observable<ActionButtonState> State { get; }

    /// <summary>Runs what the button does. Only offered while <see cref="ActionButtonState.ButtonLabel"/> is set.</summary>
    Task Invoke(CancellationToken cancellationToken);
}
