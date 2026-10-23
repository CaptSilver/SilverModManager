using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse.Mount;

public static class FuseMountArguments
{
    /// <summary>
    /// The argv <c>fuse_session_new</c> parses. It carries no mount point (<c>fuse_session_mount</c> takes that
    /// separately and the session rejects it with "unknown option(s)") and no <c>-f</c> (a <c>fuse_main</c> flag,
    /// not a session option). <c>max_read</c> is left out on purpose: it has to match what the init callback
    /// reports or libfuse refuses the mount, so the kernel implementation owns it.
    /// </summary>
    public static string[] SessionArguments(FuseMountOptions options)
    {
        var mountOptions = $"fsname={options.FsName},subtype={options.Subtype}";
        if (options.DefaultPermissions) mountOptions += ",default_permissions";
        if (options.AllowOther) mountOptions += ",allow_other";
        return ["-o", mountOptions];
    }

    /// <summary>The type <c>/proc/self/mountinfo</c> reports for a mount made with these options.</summary>
    public static string FileSystemType(FuseMountOptions options) => $"fuse.{options.Subtype}";
}
