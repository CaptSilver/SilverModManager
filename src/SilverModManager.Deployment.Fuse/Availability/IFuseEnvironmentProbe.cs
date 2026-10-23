namespace SilverModManager.Deployment.Fuse.Availability;

/// <summary>The machine facts <see cref="FuseAvailability"/> reads, behind an interface so tests can fake each one.</summary>
public interface IFuseEnvironmentProbe
{
    /// <summary>Whether <c>/dev/fuse</c> exists.</summary>
    bool DeviceExists();

    /// <summary>Whether <c>/dev/fuse</c> opens read-write. A permission failure here is the useful one.</summary>
    bool CanOpenDevice();

    /// <summary>Full path of <c>fusermount3</c> on PATH, or null.</summary>
    string? FusermountPath();

    /// <summary>Loads libfuse3 and reads <c>fuse_version()</c>; <c>error</c> says why when it could not.</summary>
    (bool loaded, string? soname, int? version, string? error) LoadLibrary();
}
