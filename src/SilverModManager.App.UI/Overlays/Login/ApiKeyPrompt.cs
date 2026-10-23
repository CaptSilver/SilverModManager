using NexusMods.Abstractions.NexusWebApi;

namespace NexusMods.App.UI.Overlays.Login;

/// <summary>
/// Asks for the API key in an overlay.
/// </summary>
public sealed class ApiKeyPrompt(IOverlayController overlayController) : IApiKeyPrompt
{
    public async ValueTask<string?> PromptAsync(Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
    {
        return await overlayController.EnqueueAndWait(new ApiKeyLoginOverlayViewModel(validate)).WaitAsync(cancellationToken);
    }
}
