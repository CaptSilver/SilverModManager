using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingSliderViewModel : IInteractionControl
{
    DoubleContainer DoubleContainer { get; }

    /// <summary>The current value as the setting's own display string, e.g. "40%".</summary>
    string ValueText { get; }
}
