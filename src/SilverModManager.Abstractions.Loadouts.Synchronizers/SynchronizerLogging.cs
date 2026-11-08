namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Log categories of the synchronizer that are kept apart from its main one because they write a line per file, on
/// every pass.
/// </summary>
public static class SynchronizerLogging
{
    /// <summary>
    /// The per-file detail of a synchronizer pass: one Trace line for each pending file, and for each file whose
    /// identity could not be joined. A game with thousands of files writes millions of these in a session, so the
    /// app hides this category even at the Trace level unless <see cref="PerFileEnvironmentVariable"/> is set. The
    /// pass itself says what it found in one summary line on the synchronizer's own category.
    /// </summary>
    public const string PerFileCategory = "SilverModManager.Abstractions.Loadouts.Synchronizers.PerFile";

    /// <summary>
    /// Set to <c>1</c> before starting the app to log <see cref="PerFileCategory"/> when the minimum logging level
    /// is Trace.
    /// </summary>
    public const string PerFileEnvironmentVariable = "SILVERMODMANAGER_LOG_PER_FILE";
}
