using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// GitHub has no requests left for this client until <see cref="ResetAt"/>. Anonymous clients get 60 an hour, shared
/// by everything on the same address.
/// </summary>
[PublicAPI]
public sealed class GitHubRateLimitedException(DateTimeOffset resetAt) : UserFacingException(
    $"GitHub's request limit is used up until {resetAt.ToLocalTime():t}. Add a token under Settings → Accounts → GitHub to raise it.")
{
    /// <summary>When GitHub starts answering again.</summary>
    public DateTimeOffset ResetAt { get; } = resetAt;

    /// <inheritdoc />
    public override string Title => "GitHub is not answering requests right now";
}
