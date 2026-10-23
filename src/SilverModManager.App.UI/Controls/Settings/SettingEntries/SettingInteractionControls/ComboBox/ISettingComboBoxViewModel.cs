using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingComboBoxViewModel : IInteractionControl
{
    string[] DisplayItems { get; }

    int SelectedItemIndex { get; set; }
}
