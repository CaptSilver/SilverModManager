using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk;

namespace SilverModManager.App.UI.Overlays.Login;

/// <summary>
/// Asks for any pasted secret in the same overlay the Nexus API key uses.
/// </summary>
public sealed class SecretPrompt(IOverlayController overlayController, IOSInterop osInterop) : ISecretPrompt
{
    public async ValueTask<string?> PromptAsync(string title, string instructions, Uri? page, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
    {
        var overlay = new ApiKeyLoginOverlayViewModel(validate, title, instructions, watermark: "Paste it here", page, osInterop.OpenUri);
        return await overlayController.EnqueueAndWait(overlay).WaitAsync(cancellationToken);
    }
}
