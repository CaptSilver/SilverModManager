using JetBrains.Annotations;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.GameBanana;

/// <summary>
/// GameBanana answered 429. The client does not ask again on its own: <see cref="RetryAfter"/> is how long the site
/// asked to be left alone, zero when it did not say.
/// </summary>
[PublicAPI]
public sealed class GameBananaRateLimitedException(TimeSpan retryAfter)
    : UserFacingException("GameBanana is asking to be left alone for a while. Try again later or open the mod's page."), IRetryAfter
{
    /// <inheritdoc />
    public override string Title => "GameBanana is not answering requests right now";

    /// <inheritdoc />
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The mod is private, withheld or gone: GameBanana answers a stub with a name and nothing else.
/// </summary>
[PublicAPI]
public sealed class GameBananaUnavailableException(long modId, string? name = null)
    : UserFacingException(name is null
        ? $"GameBanana mod {modId} is not available. It may be private, withheld or deleted."
        : $"{MarkdownText.Inline(name)} is not available on GameBanana. It may be private, withheld or deleted.")
{
    /// <summary>The mod that was asked for.</summary>
    public long ModId { get; } = modId;

    /// <inheritdoc />
    public override string Title => "The mod is not available";
}

/// <summary>
/// GameBanana answered with something that is not the listing or page the client asked for.
/// </summary>
[PublicAPI]
public sealed class GameBananaReplyUnreadableException(Uri request, Exception? inner = null)
    : UserFacingException($"GameBanana sent an answer the app could not read for {MarkdownText.Code(request.ToString())}. Try again later.", inner)
{
    /// <inheritdoc />
    public override string Title => "GameBanana's answer was not understood";
}
