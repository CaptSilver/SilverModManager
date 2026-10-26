using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>One tweak file for an INI on the Tweaks tab, as it was when the tab last loaded.</summary>
/// <param name="Name">The tweak's name.</param>
/// <param name="SourceName">The mod the tweak comes from.</param>
/// <param name="File">The loadout item holding the file.</param>
/// <param name="IsLoadoutTweak">Whether this is the loadout's own tweak.</param>
/// <param name="IsOn">The layer's own switch.</param>
/// <param name="IsActive">Whether the layer is merged into the result: its switch and its mod are both on.</param>
/// <param name="Text">What the tweak contains.</param>
public sealed record IniLayerEntry(string Name, string SourceName, EntityId File, bool IsLoadoutTweak, bool IsOn, bool IsActive, string Text);
