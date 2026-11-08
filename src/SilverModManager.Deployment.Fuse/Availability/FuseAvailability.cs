using System.Runtime.InteropServices;
using SilverModManager.Deployment.Fuse.Native;

namespace SilverModManager.Deployment.Fuse.Availability;

/// <summary>Outcome of the FUSE preflight. <see cref="Reason"/> is the first failing check, null when usable.</summary>
public sealed record FuseAvailabilityReport(bool Usable, string? Reason, IReadOnlyList<string> Lines);

public static class FuseAvailability
{
    /// <summary>Runs every check, even after a failure, so the report shows the whole picture.</summary>
    public static FuseAvailabilityReport Evaluate(IFuseEnvironmentProbe probe)
    {
        var lines = new List<string>();
        string? reason = null;

        void Pass(string line) => lines.Add($"ok: {line}");
        void Fail(string line)
        {
            lines.Add($"failed: {line}");
            reason ??= line;
        }

        var architecture = probe.ProcessArchitecture();
        var bindingsUsable = architecture == Architecture.X64;
        if (bindingsUsable) Pass("this process is x86-64");
        else Fail($"the libfuse bindings are x86-64 only; this process is {architecture}");

        var deviceExists = probe.DeviceExists();
        if (deviceExists) Pass("/dev/fuse exists");
        else Fail("/dev/fuse does not exist");

        if (!deviceExists) lines.Add("skipped: /dev/fuse open check");
        else if (probe.CanOpenDevice()) Pass("/dev/fuse can be opened");
        else Fail("/dev/fuse cannot be opened (check its permissions and your group membership)");

        if (probe.FusermountPath() is { } fusermount) Pass($"fusermount3 at {fusermount}");
        else Fail("fusermount3 is not on PATH");

        // An arm64 libfuse3 would load fine and print a passing line the x86-64-only bindings cannot use.
        var (loaded, soname, version, error) = bindingsUsable ? probe.LoadLibrary() : default;
        if (!bindingsUsable)
        {
            lines.Add("skipped: libfuse3 load");
        }
        else if (!loaded || soname is null || version is not { } fuseVersion)
        {
            Fail($"libfuse3 could not be loaded: {error ?? "no soname or version reported"}");
        }
        else
        {
            var abi = FuseAbi.Check(soname, fuseVersion);
            if (!abi.Ok) Fail($"unknown libfuse3 ABI: {abi.Reason}");
            else if (abi.NewerThanVerified) Pass($"{soname} fuse_version {fuseVersion} (newer than the verified {FuseAbi.VerifiedVersion})");
            else Pass($"{soname} fuse_version {fuseVersion}");
        }

        return new FuseAvailabilityReport(reason is null, reason, lines);
    }
}
