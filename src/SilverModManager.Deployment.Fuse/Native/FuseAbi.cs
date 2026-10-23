namespace SilverModManager.Deployment.Fuse.Native;

internal readonly record struct FuseAbiCheck(bool Ok, bool NewerThanVerified, string Reason)
{
    internal static FuseAbiCheck Accepted(bool newerThanVerified) => new(true, newerThanVerified, "");
    internal static FuseAbiCheck Refused(string reason) => new(false, false, reason);
}

/// <summary>
/// Decides whether a loaded libfuse3 is one we can drive. The soname is libfuse's ABI promise: a newer library
/// accepts an older <c>fuse_lowlevel_ops</c> table, and an incompatible layout change bumps the soname (that is
/// how <c>.so.3</c> became <c>.so.4</c>). So the rule is the soname plus a version floor, with no ceiling inside
/// the major, otherwise every routine fuse3-libs update would turn into a refused mount.
/// </summary>
internal static class FuseAbi
{
    internal const string SupportedSoname = "libfuse3.so.4";

    /// <summary><c>fuse_version()</c> is <c>FUSE_MAJOR * 100 + FUSE_MINOR</c>; 318 is libfuse 3.18.</summary>
    internal const int MinimumVersion = 318;

    /// <summary>
    /// The newest version the struct layouts were checked against. Informational: a library above it is
    /// accepted and flagged. Raise it after re-running the struct size tests against that version's headers.
    /// </summary>
    internal const int VerifiedVersion = 318;

    private const int NextMajorVersion = 400;

    internal static FuseAbiCheck Check(string soname, int version)
    {
        if (soname != SupportedSoname)
            return FuseAbiCheck.Refused($"{soname} (fuse_version {version}) is not {SupportedSoname}; a different soname means a different ABI");

        if (version < MinimumVersion || version >= NextMajorVersion)
            return FuseAbiCheck.Refused($"{soname} reports fuse_version {version}; need {MinimumVersion} or newer within libfuse 3 (below {NextMajorVersion})");

        return FuseAbiCheck.Accepted(version > VerifiedVersion);
    }
}
