using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Options for <see cref="IFuseKernel.Mount"/>. The mountinfo type is <c>fuse.&lt;Subtype&gt;</c>,
/// so the defaults give <c>fuse.silvermod</c>.
/// </summary>
public sealed record FuseMountOptions(
    AbsolutePath MountPoint,
    string FsName = "silvermod",
    string Subtype = "silvermod",
    bool AllowOther = false,
    bool DefaultPermissions = true,
    int Workers = 0)
{
    public static FuseMountOptions Default(AbsolutePath mountPoint) => new(mountPoint);
}
