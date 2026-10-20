using NexusMods.Abstractions.NexusWebApi;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// The prompt for hosts without a UI. It throws, because returning null would look like the
/// user cancelled and the sign-in would do nothing.
/// </summary>
internal sealed class NoInteractiveApiKeyPrompt : IApiKeyPrompt
{
    public ValueTask<string?> PromptAsync(CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("No interactive prompt; run `nexus-api-key <key>`");
    }
}
