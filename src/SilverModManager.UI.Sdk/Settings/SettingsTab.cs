using JetBrains.Annotations;

namespace SilverModManager.UI.Sdk.Settings;

/// <summary>The tabs of the Settings page, in the order they are shown. A section declares the one it sits on.</summary>
[PublicAPI]
public enum SettingsTab { General, Appearance, Downloads, Accounts, Developer }
