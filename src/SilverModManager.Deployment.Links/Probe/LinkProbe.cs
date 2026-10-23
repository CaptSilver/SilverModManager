using System.Security.Cryptography;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Deployment.Links.Native;
using NexusMods.Paths;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Probe;

/// <summary>
/// Tries each link kind between a store directory and a game root with a throwaway file, so a backend
/// is only chosen for a root where it demonstrably works. The probe is the truth; <see cref="SameDevice"/>
/// is a fast pre-check that lets a refusal say "different device" before trying.
/// </summary>
/// <remarks>
/// Roots such as Preferences and My Games often do not exist before the first extract, and link(2)
/// into a missing directory is ENOENT. Every probe therefore runs in the nearest existing ancestor of
/// the root. Probe files carry the write probe's name prefix so a copy left behind by a crash is
/// skipped by the indexer instead of being ingested as the player's file.
/// </remarks>
internal static class LinkProbe
{
    private static readonly byte[] Payload = [0x5A];

    /// <summary>
    /// Whether the nearest existing ancestors of both paths report the same <c>st_dev</c>. Not
    /// <c>statfs</c>'s <c>f_fsid</c>: btrfs subvolumes share a filesystem id but report different
    /// devices, and link(2) fails EXDEV across them. False when either device cannot be read.
    /// </summary>
    internal static bool SameDevice(AbsolutePath a, AbsolutePath b) =>
        TryDevice(a, out var deviceA) && TryDevice(b, out var deviceB) && deviceA == deviceB;

    /// <summary>Probes link(2) from the store directory into the root.</summary>
    internal static bool TryHardlink(AbsolutePath storeDirectory, AbsolutePath root, out string reason)
    {
        return Run(storeDirectory, root, out reason, (source, destination, differentDevice) =>
        {
            var errno = LibcLinks.TryLink(source, destination);
            if (errno != 0)
                return Refuse($"link(2) failed: {ErrnoText.Describe(errno)}", differentDevice);

            return Same(File.ReadAllBytes(destination), "link(2)");
        });
    }

    /// <summary>Probes <c>ioctl(FICLONE)</c> from the store directory into the root.</summary>
    internal static bool TryReflink(AbsolutePath storeDirectory, AbsolutePath root, out string reason)
    {
        return Run(storeDirectory, root, out reason, (source, destination, differentDevice) =>
        {
            int errno;
            using (var from = File.OpenHandle(source))
            using (var to = File.OpenHandle(destination, FileMode.CreateNew, FileAccess.Write))
                errno = LibcLinks.TryClone(from, to);

            if (errno != 0)
                return Refuse($"ioctl(FICLONE) failed: {ErrnoText.Describe(errno, LibcLinks.EOPNOTSUPP, "filesystem has no reflink")}", differentDevice);

            return Same(File.ReadAllBytes(destination), "ioctl(FICLONE)");
        });
    }

    /// <summary>Probes symlink(2) pointing from the root at the store directory.</summary>
    internal static bool TrySymlink(AbsolutePath storeDirectory, AbsolutePath root, out string reason)
    {
        return Run(storeDirectory, root, out reason, (source, destination, differentDevice) =>
        {
            var errno = LibcLinks.TrySymlink(source, destination);
            if (errno != 0)
                return Refuse($"symlink(2) failed: {ErrnoText.Describe(errno)}", differentDevice);

            errno = LibcLinks.TryReadLink(destination, out var target);
            if (errno != 0)
                return Refuse($"readlink(2) failed: {ErrnoText.Describe(errno)}", differentDevice);
            if (target != source)
                return Refuse($"readlink(2) returned '{target}' for a link to '{source}'", differentDevice);

            return Same(File.ReadAllBytes(destination), "symlink(2)");
        });
    }

    /// <summary>
    /// Writes the source probe file, runs <paramref name="attempt"/> with the destination path in the
    /// root's nearest existing ancestor, and deletes both files whatever happens. The attempt returns
    /// null when the link works, else the refusal text.
    /// </summary>
    private static bool Run(AbsolutePath storeDirectory, AbsolutePath root, out string reason, Func<string, string, bool, string?> attempt)
    {
        var name = $"{ProtonMessages.ProbeFileName}-{Guid.NewGuid():N}";
        var source = Path.Combine(storeDirectory.ToString(), name);
        var destination = Path.Combine(NearestExisting(root), name);
        var differentDevice = !SameDevice(storeDirectory, root);

        try
        {
            Directory.CreateDirectory(storeDirectory.ToString());
            File.WriteAllBytes(source, Payload);

            var refusal = attempt(source, destination, differentDevice);
            reason = refusal ?? "accepted";
            return refusal is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reason = $"probe failed: {e.Message}";
            return false;
        }
        finally
        {
            TryDelete(destination);
            TryDelete(source);
        }
    }

    private static string? Refuse(string text, bool differentDevice) =>
        differentDevice ? $"{text}; the store and the game folder are on different devices" : text;

    private static string? Same(byte[] read, string operation) =>
        read.AsSpan().SequenceEqual(Payload) ? null : $"{operation} produced a file with different contents";

    private static bool TryDevice(AbsolutePath path, out ulong device)
    {
        if (LibcStat.TryStat(NearestExisting(path), out var stat) == 0)
        {
            device = stat.Device;
            return true;
        }

        device = 0;
        return false;
    }

    private static string NearestExisting(AbsolutePath path)
    {
        var current = path.ToString();
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }

        return current;
    }

    // A probe file that cannot be removed is reported by the next probe's own failure; a delete error
    // must not hide the verdict.
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
