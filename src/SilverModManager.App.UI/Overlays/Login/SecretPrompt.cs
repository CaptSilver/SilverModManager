using SilverModManager.Abstractions.Accounts;

namespace SilverModManager.App.UI.Overlays.Login;

/// <summary>
/// Asks for any pasted secret in the same overlay the Nexus API key uses.
/// </summary>
public sealed class SecretPrompt(IOverlayController overlayController) : ISecretPrompt
{
    public async ValueTask<string?> PromptAsync(string title, string instructions, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
    {
        var overlay = new ApiKeyLoginOverlayViewModel(validate, title, instructions, watermark: "Paste it here");
        return await overlayController.EnqueueAndWait(overlay).WaitAsync(cancellationToken);
    }
}
