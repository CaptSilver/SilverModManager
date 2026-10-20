using NexusMods.Abstractions.NexusWebApi;

namespace NexusMods.App.UI.Overlays.Login;

/// <summary>
/// Asks for the API key in an overlay.
/// </summary>
public sealed class ApiKeyPrompt(IOverlayController overlayController) : IApiKeyPrompt
{
    public async ValueTask<string?> PromptAsync(CancellationToken cancellationToken)
    {
        return await overlayController.EnqueueAndWait(new ApiKeyLoginOverlayViewModel()).WaitAsync(cancellationToken);
    }
}
