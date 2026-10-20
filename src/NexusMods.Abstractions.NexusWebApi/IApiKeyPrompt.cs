namespace NexusMods.Abstractions.NexusWebApi;

/// <summary>
/// Asks the user for a personal Nexus Mods API key.
/// </summary>
public interface IApiKeyPrompt
{
    /// <summary>
    /// Asks for a key. Returns null when the user cancelled. Hosts with nobody to ask throw
    /// instead of returning null, so a sign-in attempt never fails silently.
    /// </summary>
    ValueTask<string?> PromptAsync(CancellationToken cancellationToken);
}
