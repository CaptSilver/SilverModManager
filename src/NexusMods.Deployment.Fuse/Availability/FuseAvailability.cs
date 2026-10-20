using NexusMods.Deployment.Fuse.Native;

namespace NexusMods.Deployment.Fuse.Availability;

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

        var deviceExists = probe.DeviceExists();
        if (deviceExists) Pass("/dev/fuse exists");
        else Fail("/dev/fuse does not exist");

        if (!deviceExists) lines.Add("skipped: /dev/fuse open check");
        else if (probe.CanOpenDevice()) Pass("/dev/fuse can be opened");
        else Fail("/dev/fuse cannot be opened (check its permissions and your group membership)");

        if (probe.FusermountPath() is { } fusermount) Pass($"fusermount3 at {fusermount}");
        else Fail("fusermount3 is not on PATH");

        var (loaded, soname, version, error) = probe.LoadLibrary();
        if (!loaded || soname is null || version is not { } fuseVersion)
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
