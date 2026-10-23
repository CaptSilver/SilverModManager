using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingToggleViewModel : IInteractionControl
{
    BooleanContainer BooleanContainer { get; }
}
