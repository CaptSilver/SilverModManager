namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>Whether a read of the merge inputs may keep anything on disk.</summary>
public enum MergeSourceMode
{
    /// <summary>The deployment is being built, so a vanilla regulation worth keeping is kept.</summary>
    Apply,

    /// <summary>Planning, Health Check or a report: nothing is written.</summary>
    Report,
}
