using JetBrains.Annotations;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.ModIo;

/// <summary>
/// There is no mod.io token in the secret store. mod.io answers nothing without one, so nothing was asked.
/// </summary>
[PublicAPI]
public sealed class ModIoNotSignedInException()
    : UserFacingException($"mod.io needs your access token. Paste one under Settings → Accounts → {ModIoAccount.Name}; create it at mod.io/me/access with the read and write scopes.")
{
    /// <inheritdoc />
    public override string Title => "mod.io is not signed in";
}

/// <summary>mod.io did not accept the stored token: it expired, was revoked or is not a token.</summary>
[PublicAPI]
public sealed class ModIoTokenRejectedException()
    : UserFacingException($"mod.io did not accept the stored access token. Paste a new one under Settings → Accounts → {ModIoAccount.Name}.")
{
    /// <inheritdoc />
    public override string Title => "mod.io rejected the token";
}

/// <summary>mod.io refused the request with a 403 that is not about the terms.</summary>
[PublicAPI]
public sealed class ModIoRefusedException(string message) : UserFacingException(message)
{
    /// <summary>The refusal of a token that was created without the write scope, which cannot subscribe.</summary>
    public static ModIoRefusedException ReadOnlyToken()
        => new("This token cannot subscribe; create one with the write scope at mod.io/me/access.");

    /// <inheritdoc />
    public override string Title => "mod.io refused the request";
}

/// <summary>The account has not accepted mod.io's terms, which mod.io requires before it answers write requests.</summary>
[PublicAPI]
public sealed class ModIoTermsNotAcceptedException()
    : UserFacingException("mod.io says this account has not agreed to its terms yet. Read and accept them at docs.mod.io/terms, then try again.")
{
    /// <inheritdoc />
    public override string Title => "mod.io wants its terms accepted";
}

/// <summary>
/// mod.io answered 429. The client does not ask again on its own, because continuing after a 429 can get the
/// credentials revoked: <see cref="RetryAfter"/> is how long mod.io asked to be left alone, zero when it did not say.
/// </summary>
[PublicAPI]
public sealed class ModIoRateLimitedException(TimeSpan retryAfter)
    : UserFacingException("mod.io is asking to be left alone for a while. Try again later."), IRetryAfter
{
    /// <inheritdoc />
    public override string Title => "mod.io is not answering requests right now";

    /// <inheritdoc />
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>mod.io could not be reached or answered with an error this client has no better words for.</summary>
[PublicAPI]
public sealed class ModIoUnavailableException(string message, Exception? inner = null) : UserFacingException(message, inner)
{
    /// <inheritdoc />
    public override string Title => "mod.io did not answer";
}

/// <summary>mod.io answered with something that is not the listing or object the client asked for.</summary>
[PublicAPI]
public sealed class ModIoReplyUnreadableException(Uri request, Exception? inner = null)
    : UserFacingException($"mod.io sent an answer the app could not read for {MarkdownText.Code(request.AbsolutePath)}. Try again later.", inner)
{
    /// <inheritdoc />
    public override string Title => "mod.io's answer was not understood";
}
