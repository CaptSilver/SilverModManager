namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// The modlist cannot be read in full: an unknown directive type, a missing required field, or not a
/// modlist at all. A list the app cannot execute completely must not be half-installed, so loading fails.
/// </summary>
public class UnsupportedModlistException(string what) : Exception(what);
