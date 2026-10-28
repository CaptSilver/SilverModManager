using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public interface ISettingTextViewModel : IInteractionControl
{
    TextContainer TextContainer { get; }
}
