using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Accounts;

/// <summary>
/// The prompt for hosts without a window. It throws, because returning null would look like the user cancelled and
/// the sign-in would quietly do nothing.
/// </summary>
public sealed class HeadlessSecretPrompt : ISecretPrompt
{
    public ValueTask<string?> PromptAsync(string title, string instructions, Uri? page, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
        => throw new SecretPromptUnavailableException(title);
}

/// <summary>A secret was asked for where nobody can be asked.</summary>
public sealed class SecretPromptUnavailableException(string settingsRow)
    : UserFacingException($"Open Settings → Accounts → {MarkdownText.InlineMidLine(settingsRow)} in the app to add it.")
{
    public override string Title => "Not signed in";
}
