namespace SilverModManager.Abstractions.Accounts;

/// <summary>
/// Asks the user to paste a secret. The app shows an overlay; a host without a window refuses and names the Settings
/// row to use instead.
/// </summary>
public interface ISecretPrompt
{
    /// <param name="title">What is being asked for. It is also the name of the Settings → Accounts row that takes it.</param>
    /// <param name="instructions">Where to get the secret and what to do with it.</param>
    /// <param name="page">The page the secret is made on, shown as a link that opens in the browser; null for none.</param>
    /// <param name="validate">
    /// Checks a pasted value and returns the reason it is unusable, or null when it is fine. The prompt accepts a value
    /// only once this returns null and shows the reason while it stays open.
    /// </param>
    /// <returns>The accepted value, trimmed, or null when the user cancelled.</returns>
    ValueTask<string?> PromptAsync(string title, string instructions, Uri? page, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken);
}
