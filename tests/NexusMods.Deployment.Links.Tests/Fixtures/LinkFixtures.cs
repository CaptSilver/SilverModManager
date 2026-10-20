using NexusMods.Deployment.Links.Native;
using NexusMods.Paths;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Tests.Fixtures;

/// <summary>
/// Throwaway directories on known filesystems. Each one deletes itself on dispose; none of them
/// touches a path the test did not create.
/// </summary>
public sealed class LinkFixtures : IDisposable
{
    private const string SharedMemory = "/dev/shm";
    private readonly List<string> _created = [];

    /// <summary>A fresh directory on the filesystem holding the temp directory.</summary>
    public AbsolutePath SameDeviceRoot() => Create(TempDirectory().ToString());

    /// <summary>
    /// A fresh directory on a different device than <see cref="SameDeviceRoot"/>, so link(2) and FICLONE
    /// between the two fail with EXDEV. Skips when /dev/shm is missing or shares the temp device.
    /// </summary>
    public AbsolutePath CrossDeviceRoot()
    {
        Assert.SkipUnless(Directory.Exists(SharedMemory), $"{SharedMemory} does not exist");
        Assert.Equal(0, LibcStat.TryStat(SharedMemory, out var shm));
        Assert.Equal(0, LibcStat.TryStat(TempDirectory().ToString(), out var temp));
        Assert.SkipWhen(shm.Device == temp.Device, $"{SharedMemory} is on the same device as the temp directory");
        return Create(SharedMemory);
    }

    /// <summary>
    /// A fresh directory inside the test assembly's output, which is on the checkout's filesystem
    /// (btrfs on the dev box, where FICLONE works). Skips when that filesystem cannot reflink.
    /// </summary>
    public AbsolutePath ReflinkRoot()
    {
        var parent = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).ToString();
        var root = Create(parent);
        Assert.SkipUnless(CanReflink(root.ToString()), $"FICLONE is not supported on the filesystem holding the test output ({parent})");
        return root;
    }

    public void Dispose()
    {
        foreach (var path in _created)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static AbsolutePath TempDirectory() => FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory);

    private AbsolutePath Create(string parent)
    {
        var path = Path.Combine(parent, $"links-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _created.Add(path);
        return FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    private static bool CanReflink(string directory)
    {
        var source = Path.Combine(directory, "clone-source");
        var destination = Path.Combine(directory, "clone-destination");
        File.WriteAllText(source, "x");
        try
        {
            using var from = File.OpenHandle(source);
            using var to = File.OpenHandle(destination, FileMode.Create, FileAccess.Write);
            return LibcLinks.TryClone(from, to) == 0;
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }
}
