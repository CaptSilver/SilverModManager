using NexusMods.Abstractions.NexusWebApi;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// The prompt for hosts without a UI. It throws, because returning null would look like the
/// user cancelled and the sign-in would do nothing.
/// </summary>
internal sealed class NoInteractiveApiKeyPrompt : IApiKeyPrompt
{
    public ValueTask<string?> PromptAsync(Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("No interactive prompt; pipe the key to `nexus-api-key`");
    }
}
