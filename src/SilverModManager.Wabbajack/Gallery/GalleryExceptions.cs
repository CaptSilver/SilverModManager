using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>A gallery document is not the JSON shape it is supposed to be.</summary>
public sealed class GalleryFormatException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The modlist gallery could not be read";
}

/// <summary>The gallery could not be read from the network and no cached copy exists.</summary>
public sealed class GalleryUnavailableException(string message, Exception? inner = null) : UserFacingException(message, inner)
{
    /// <inheritdoc />
    public override string Title => "The modlist gallery is unavailable";
}

/// <summary>A list could not be downloaded, or what arrived was not the file the gallery described.</summary>
public sealed class GalleryDownloadException(string message, Exception? inner = null) : UserFacingException(message, inner)
{
    /// <inheritdoc />
    public override string Title => "The modlist could not be downloaded";
}
