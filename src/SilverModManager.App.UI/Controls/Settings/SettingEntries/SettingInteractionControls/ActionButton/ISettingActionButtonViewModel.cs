using System.Reactive;
using ReactiveUI;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingActionButtonViewModel : IInteractionControl
{
    ActionButtonContainer ActionContainer { get; }

    /// <summary>What the row says now.</summary>
    string Text { get; }

    /// <summary>The button's label, or null when the row offers no action.</summary>
    string? ButtonLabel { get; }

    bool HasButton { get; }

    ReactiveCommand<Unit, Unit> InvokeCommand { get; }
}
