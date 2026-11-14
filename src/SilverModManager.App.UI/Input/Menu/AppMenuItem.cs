namespace SilverModManager.App.UI.Input.Menu;

/// <summary>
/// One entry of the pad's app menu. An entry that cannot run now stays in the list with the reason, so the menu does
/// not change shape from page to page.
/// </summary>
public sealed record AppMenuItem(string Title, Action Run, string? DisabledReason = null)
{
    public bool IsEnabled => DisabledReason is null;
}
