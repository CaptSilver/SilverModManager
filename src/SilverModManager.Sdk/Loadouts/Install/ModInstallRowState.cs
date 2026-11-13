namespace SilverModManager.Sdk.Loadouts.Install;

/// <summary>
/// One reading of a <see cref="ModInstallRow"/>. It is immutable so a reader on another thread never sees a state
/// half-way through a change.
/// </summary>
/// <param name="Resolved">Files of the folder located in their archives so far.</param>
/// <param name="Written">Files of the folder written to the loadout so far.</param>
/// <param name="Total">Files the folder has.</param>
/// <param name="Reason">Why the folder failed; only set on <see cref="ModInstallState.Failed"/>.</param>
/// <param name="Detail">Extra text for the row, such as the archive being built or the pass it waits for.</param>
public sealed record ModInstallRowState(ModInstallState State, int Resolved, int Written, int Total, string? Reason = null, string? Detail = null)
{
    /// <summary>The count the row shows: files located while extracting, files written afterwards.</summary>
    public int Done => State == ModInstallState.Extracting ? Resolved : Written;
}
