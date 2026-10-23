namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// How far a phase of an apply has got. A total of <c>null</c> means the phase cannot say yet, and shows as indeterminate.
/// </summary>
public readonly record struct SyncProgress(string Phase, long FilesDone, long? FilesTotal, long BytesDone, long? BytesTotal)
{
    /// <summary>
    /// A phase that reports no counts.
    /// </summary>
    public static SyncProgress Indeterminate(string phase) => new(phase, 0, null, 0, null);

    /// <summary>
    /// Done out of total in [0, 1], or <c>null</c> when no total is known. Bytes are used when both sides know
    /// them: one huge file would otherwise count the same as a tiny one.
    /// </summary>
    public double? Fraction
    {
        get
        {
            if (BytesTotal is > 0) return Math.Clamp((double)BytesDone / BytesTotal.Value, 0, 1);
            if (FilesTotal is > 0) return Math.Clamp((double)FilesDone / FilesTotal.Value, 0, 1);
            return null;
        }
    }

    /// <summary>
    /// The phase name, followed by the file count when there is one.
    /// </summary>
    public string Text => Phase is null ? "" : FilesTotal is > 0 ? $"{Phase} ({FilesDone}/{FilesTotal})" : Phase;
}

/// <summary>
/// The phase names an apply reports.
/// </summary>
public static class SyncPhases
{
    public const string Collecting = "Collecting files";
    public const string BackingUp = "Backing up files";
    public const string IngestingChanges = "Adding external changes";
    public const string UpdatingLoadout = "Updating loadout";
    public const string Deleting = "Deleting files";
    public const string Deploying = "Extracting files";
    public const string WritingIntrinsics = "Writing intrinsic files";
    public const string UpdatingDeletedFiles = "Updating deleted files";
    public const string RecordingChanges = "Recording changes";
    public const string CleaningUp = "Archive Cleanup";
}
