using NexusMods.Deployment.Fuse.Native;

namespace NexusMods.Deployment.Fuse.Availability;

/// <summary>Reads the real machine.</summary>
public sealed class FuseEnvironmentProbe : IFuseEnvironmentProbe
{
    private const string DevicePath = "/dev/fuse";

    public bool DeviceExists() => File.Exists(DevicePath);

    public bool CanOpenDevice()
    {
        try
        {
            using var stream = new FileStream(DevicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? FusermountPath() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, "fusermount3"))
        .FirstOrDefault(File.Exists);

    public (bool loaded, string? soname, int? version, string? error) LoadLibrary()
    {
        try
        {
            var version = LibFuse3.fuse_version();
            return (true, LibFuse3Resolver.LoadedSoname, version, null);
        }
        catch (DllNotFoundException e)
        {
            return (false, null, null, e.Message);
        }
    }
}
