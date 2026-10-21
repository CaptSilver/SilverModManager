using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>
/// The command that runs me3 inside a bwrap sandbox with a writable overlay on the game folder, built from one
/// constant per group of flags so a correction is a change to a constant.
/// </summary>
/// <remarks>
/// This shape has not run against the real game. Three things decide whether it works, and none can be checked
/// without a played session: a nested user namespace under me3's own Proton chain, whether Steam's IPC sockets
/// and <c>/run/user</c> stay reachable through the bind of <c>/</c>, and whether the game and its anti-cheat see the
/// overlay mounted at the real game path. The me3 flags after the separator are not verified against me3 0.13
/// either; erm's own command is <c>me3 launch -p &lt;profile&gt;</c> and nothing more.
/// </remarks>
public static class OverlayCommand
{
    public const string Program = "bwrap";

    /// <summary>Separates bwrap's own flags from the command it runs.</summary>
    public const string Separator = "--";

    /// <summary>Starts the sandbox in a new user namespace, which is what lets an unprivileged user mount.</summary>
    public static readonly string[] UserNamespace = ["--unshare-user"];

    /// <summary>The root is bound through unchanged, so the game and me3 see the whole machine as they do outside.</summary>
    public static readonly string[] RootBind = ["--bind", "/", "/"];

    public static readonly string[] DeviceBind = ["--dev-bind", "/dev", "/dev"];

    public static readonly string[] ProcMount = ["--proc", "/proc"];

    /// <summary>The flag before the lower folder: marks it as the overlay's read-only source.</summary>
    public const string OverlaySourceFlag = "--overlay-src";

    /// <summary>The flag before upper, work and the mount target.</summary>
    public const string OverlayFlag = "--overlay";

    public const string UidFlag = "--uid";
    public const string GidFlag = "--gid";

    /// <summary>The me3 verb and the profile flag.</summary>
    public static readonly string[] Me3Launch = ["launch", "-p"];

    /// <summary>Names the game to launch. Unverified against me3 0.13.</summary>
    public static readonly string[] GameSelection = ["-g", "eldenring"];

    /// <summary>Where the Windows halves of me3 are. Unverified against me3 0.13.</summary>
    public static readonly string[] WindowsBinariesFlag = ["--windows-binaries-dir"];

    /// <summary>
    /// The whole command, program first. The overlay is mounted onto <see cref="OverlayLayout.Lower"/> itself, so
    /// the game finds its files at the path it was installed to.
    /// </summary>
    /// <param name="layout">Where the overlay's folders are.</param>
    /// <param name="uid">The user id the sandbox presents. The mapping keeps files owned by the same user inside.</param>
    /// <param name="gid">The group id the sandbox presents.</param>
    /// <param name="me3Path">The me3 binary of the loadout's host files.</param>
    /// <param name="profilePath">The generated me3 profile.</param>
    /// <param name="windowsBinDir">The folder with me3's Windows components.</param>
    public static string[] Build(OverlayLayout layout, uint uid, uint gid, AbsolutePath me3Path, AbsolutePath profilePath, AbsolutePath windowsBinDir)
    {
        var lower = layout.Lower.GetFullPath();
        return
        [
            Program,
            ..UserNamespace,
            UidFlag, uid.ToString(),
            GidFlag, gid.ToString(),
            ..RootBind,
            ..DeviceBind,
            ..ProcMount,
            OverlaySourceFlag, lower,
            OverlayFlag, layout.Upper.GetFullPath(), layout.Work.GetFullPath(), lower,
            Separator,
            me3Path.GetFullPath(),
            ..Me3Launch, profilePath.GetFullPath(),
            ..GameSelection,
            ..WindowsBinariesFlag, windowsBinDir.GetFullPath(),
        ];
    }
}
