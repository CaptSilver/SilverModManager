namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// One entry of a store page's sort box. <see cref="Key"/> is what the page stores and reads back (the name of
/// its sort enum member); <see cref="Label"/> is what the user sees.
/// </summary>
public sealed record StoreSortOption(string Key, string Label);
