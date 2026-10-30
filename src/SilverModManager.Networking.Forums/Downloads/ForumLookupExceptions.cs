using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Forums.Downloads;

/// <summary>
/// The forum answered the files lookup with 403: the account may not download this file.
/// </summary>
public sealed class ForumRefusedException(string siteName) : UserFacingException($"{siteName} refused the request (403). Download it from the page.")
{
    public override string Title => "Download not completed";
}

/// <summary>
/// The forum is rate-limiting the lookups and kept doing so through every retry.
/// </summary>
public sealed class ForumRateLimitedException(string siteName, TimeSpan retryAfter)
    : UserFacingException($"{siteName} is rate-limiting downloads. Try again later or download it from the page."), IRetryAfter
{
    public override string Title => "Download not completed";

    /// <summary>
    /// How long the forum asked to be left alone; zero when it did not say.
    /// </summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The file listing does not name the file the modlist expects.
/// </summary>
public sealed class ForumFileNotListedException(string siteName, string fileName) : UserFacingException($"{siteName} no longer lists '{fileName}' under this file")
{
    public override string Title => "Download not completed";
}

/// <summary>
/// The file listing names the file more than once, so there is no telling which is meant.
/// </summary>
public sealed class ForumFileAmbiguousException(string siteName, string fileName)
    : UserFacingException($"{siteName} lists '{fileName}' more than once under this file; download it from the page")
{
    public override string Title => "Download not completed";
}

/// <summary>
/// The listed file is not the size the modlist expects, so it is not the file the list was built from.
/// </summary>
public sealed class ForumFileSizeMismatchException(string siteName, string fileName)
    : UserFacingException($"{siteName}'s copy of '{fileName}' is a different size than the list expects")
{
    public override string Title => "Download not completed";
}

/// <summary>
/// The forum answered the files lookup with something that is not a file listing.
/// </summary>
public sealed class ForumReplyUnreadableException(string siteName, Exception? inner = null)
    : UserFacingException($"{siteName} sent a file listing the app could not read; download it from the page", inner)
{
    public override string Title => "Download not completed";
}
