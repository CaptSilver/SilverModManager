using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>A modlist was not reinstalled, with the reason, before anything was removed.</summary>
public sealed class ModlistReinstallRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The modlist was not reinstalled";
}
