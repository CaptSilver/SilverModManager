using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Resources;

namespace SilverModManager.App.UI.Overlays.Login;

/// <summary>
/// Asks for the Nexus Mods API key in an overlay.
/// </summary>
public sealed class ApiKeyPrompt(IOverlayController overlayController) : IApiKeyPrompt
{
    /// <summary>
    /// The page on Nexus Mods where a personal API key is created.
    /// </summary>
    private const string KeyPageUrl = "https://www.nexusmods.com/users/myaccount?tab=api";

    public async ValueTask<string?> PromptAsync(Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
    {
        var overlay = new ApiKeyLoginOverlayViewModel(
            validate,
            Language.ApiKeyLoginOverlayView_Title,
            Language.ApiKeyLoginOverlayView_Description,
            Language.ApiKeyLoginOverlayView_Watermark,
            KeyPageUrl);
        return await overlayController.EnqueueAndWait(overlay).WaitAsync(cancellationToken);
    }
}
