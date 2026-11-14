using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Deployment.Fuse.Availability;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A machine that has everything FUSE needs. The fuse deployment backend reads the probe when it checks a root, and the
/// machine running the tests may have no FUSE; nothing here mounts.
/// </summary>
public sealed class UsableFuseProbe : IFuseEnvironmentProbe
{
    public Architecture ProcessArchitecture() => Architecture.X64;

    public bool DeviceExists() => true;

    public bool CanOpenDevice() => true;

    public string FusermountPath() => "/usr/bin/fusermount3";

    public (bool loaded, string? soname, int? version, string? error) LoadLibrary() => (true, "libfuse3.so.4", 318, null);
}

public static class UsableFuseProbeServices
{
    /// <summary>Replaces the real probe with <see cref="UsableFuseProbe"/>.</summary>
    public static IServiceCollection AddUsableFuseProbe(this IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton<IFuseEnvironmentProbe>(new UsableFuseProbe()));
}
