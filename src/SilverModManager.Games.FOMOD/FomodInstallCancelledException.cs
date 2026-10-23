namespace SilverModManager.Games.FOMOD;

/// <summary>
/// The user cancelled, or closed the window of, an interactive FOMOD installation.
/// </summary>
public sealed class FomodInstallCancelledException : Exception
{
    /// <summary>
    /// Constructor.
    /// </summary>
    public FomodInstallCancelledException(string message) : base(message) { }
}
