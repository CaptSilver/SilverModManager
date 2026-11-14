using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

/// <summary>
/// Holds the setting a status-and-button row sits on. The row never edits it, so it never reads as changed, and it
/// does not care what type it is: a source with nothing of its own to store hangs its row on any setting it has.
/// </summary>
[PublicAPI]
public class ActionButtonContainer : APropertyValueContainer<object, ActionButtonContainerOptions>
{
    public ActionButtonContainer(object value, object defaultValue, PropertyConfig config) : base(value, defaultValue, config) { }
}
