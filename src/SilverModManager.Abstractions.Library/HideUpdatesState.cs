namespace SilverModManager.Abstractions.Library;

/// <summary>
/// How the hide-updates menu item of a row looks and whether it applies.
/// </summary>
/// <param name="IsHidden">The row's updates are hidden, so the item offers to show them again.</param>
/// <param name="ItemCount">How many items the action covers, shown beside the label.</param>
/// <param name="IsEnabled">The item can be clicked.</param>
/// <param name="IsVisible">The item is in the menu at all.</param>
public readonly record struct HideUpdatesState(bool IsHidden, int ItemCount, bool IsEnabled, bool IsVisible);
