namespace SilverModManager.Abstractions.Library;

/// <summary>
/// Which wording the hide-updates menu item takes. A source names the wording and the UI looks the text up in the
/// language resources, so the item follows the user's language whatever the source.
/// </summary>
public enum HideUpdatesLabelKind
{
    /// <summary>"Hide updates": the item stops offering them.</summary>
    Hide,

    /// <summary>"Show updates": the item offers hidden updates again.</summary>
    Show,

    /// <summary>"Freeze version": the item stays on its version, which the update check and the lock honour.</summary>
    Freeze,

    /// <summary>"Unfreeze version": the item may move to newer versions again.</summary>
    Unfreeze,
}
