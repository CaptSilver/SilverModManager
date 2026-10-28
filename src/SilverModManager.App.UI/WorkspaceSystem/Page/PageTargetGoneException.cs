namespace SilverModManager.App.UI.WorkspaceSystem;

/// <summary>
/// Thrown by a page factory when the thing its page shows no longer exists, e.g. a saved tab for a collection the
/// user has since removed. Restoring a workspace skips such a tab quietly instead of reporting a broken page.
/// </summary>
public class PageTargetGoneException(string message) : Exception(message);
