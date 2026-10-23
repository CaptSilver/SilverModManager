namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>
/// How the overlay over the game folder is made. A FUSE overlay (fuse-overlayfs) for kernels that refuse an
/// unprivileged one is a possible later addition; until then a kernel that refuses the mount means the launch is
/// refused, never that the game runs against the real folder.
/// </summary>
public enum OverlayStrategy
{
    /// <summary>A kernel overlay mounted by bwrap inside a user namespace.</summary>
    Kernel,
}
