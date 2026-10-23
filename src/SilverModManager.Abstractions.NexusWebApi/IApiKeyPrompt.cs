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
    /// <param name="validate">
    /// Checks a typed key against Nexus Mods and returns a message for the user when the key is
    /// not usable, or null when it is. The prompt only accepts a key once this returns null, and
    /// shows the message while it stays open, so a bad paste is never a silent close.
    /// </param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    ValueTask<string?> PromptAsync(Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken);
}
