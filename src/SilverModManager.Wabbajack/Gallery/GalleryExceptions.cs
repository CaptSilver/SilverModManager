namespace SilverModManager.Wabbajack.Gallery;

/// <summary>A gallery document is not the JSON shape it is supposed to be.</summary>
public sealed class GalleryFormatException(string message) : Exception(message);

/// <summary>The gallery could not be read from the network and no cached copy exists.</summary>
public sealed class GalleryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A list could not be downloaded, or what arrived was not the file the gallery described.</summary>
public sealed class GalleryDownloadException(string message, Exception? inner = null) : Exception(message, inner);
