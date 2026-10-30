using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

/// <summary>
/// Holds the setting a status-and-button row sits on. The row never edits it, so it never reads as changed.
/// </summary>
[PublicAPI]
public class ActionButtonContainer : APropertyValueContainer<string, ActionButtonContainerOptions>
{
    public ActionButtonContainer(string value, string defaultValue, PropertyConfig config) : base(value, defaultValue, config) { }
}
