using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Deployment.Fuse.Kernel;
using NexusMods.Deployment.Fuse.Mount;
using NexusMods.Deployment.Fuse.Native;
using NexusMods.Deployment.Vfs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Deployment.Fuse.Tests;

/// <summary>
/// Mounts a real <see cref="VfsTree"/> through libfuse and drives it with ordinary file calls from this
/// process. Every mount lives in an empty folder under the test's own temp directory.
/// </summary>
public class MountRoundTripTests(ITestOutputHelper output) : IDisposable
{
    private const string MountInfo = "/proc/self/mountinfo";

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-roundtrip-{Guid.NewGuid():N}");

    private readonly List<string> _mountPoints = [];

    public void Dispose()
    {
        // A failed test must not leave a mount behind in the temp folder.
        foreach (var mountPoint in _mountPoints.Where(IsListed)) ForceUnmount(mountPoint);
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }

    private sealed record Mounted(IFuseMount Mount, AbsolutePath MountPoint, StagingDirectory Staging, AbsolutePath Backing);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static LibFuseLowLevelKernel NewKernel() =>
        new(NullLogger<LibFuseLowLevelKernel>.Instance, new FusermountUnmounter(new CliFusermountRunner()));

    private async Task<Mounted> MountTree(params (string Path, byte[] Content)[] staged)
    {
        var backing = _directory.Combine("game");
        var loose = _directory.Combine("loose");
        var mountPoint = _directory.Combine("mount");
        backing.Combine("Data").CreateDirectory();
        loose.CreateDirectory();
        mountPoint.CreateDirectory();
        await backing.Combine("Data/vanilla.txt").WriteAllBytesAsync(Bytes("vanilla"));

        var sources = new List<VfsFileSource>();
        foreach (var (path, content) in staged)
        {
            var file = loose.Combine(Guid.NewGuid().ToString("N"));
            await file.WriteAllBytesAsync(content);
            sources.Add(new VfsFileSource(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Staged, file, Size.From((ulong)content.Length), Hash.From(2)));
        }

        var staging = new StagingDirectory(_directory.Combine("staging"), GameInstallMetadataId.From(0xABC));
        var tree = VfsTree.Build(backing, new BackingScanner().Scan(backing), sources, staging, VfsCachePolicy.Default);

        _mountPoints.Add(mountPoint.ToString());
        var mount = await NewKernel().Mount(tree, FuseMountOptions.Default(mountPoint), CancellationToken.None);
        return new Mounted(mount, mountPoint, staging, backing);
    }

    private static async Task Unmount(Mounted mounted)
    {
        try
        {
            await mounted.Mount.Unmount(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        catch
        {
            if (IsListed(mounted.MountPoint.ToString())) ForceUnmount(mounted.MountPoint.ToString());
            throw;
        }
    }

    private static bool IsListed(string mountPoint) =>
        File.ReadLines(MountInfo).Any(line => line.Contains($"/{Path.GetFileName(mountPoint)} ", StringComparison.Ordinal) && line.Contains(" - fuse.silvermod ", StringComparison.Ordinal));

    private static void ForceUnmount(string mountPoint)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start("fusermount3", ["-uz", "--", mountPoint]);
            process?.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Nothing is mounted, or fusermount3 is missing; either way there is nothing left to undo.
        }
    }

    private static bool IsMounted(AbsolutePath mountPoint) => IsListed(mountPoint.ToString());

    [Fact, Trait("RequiresFuse", "True")]
    public async Task ListsExactlyTheServedFiles()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            var files = Directory.EnumerateFiles(mounted.MountPoint.ToString(), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(mounted.MountPoint.ToString(), f))
                .Order()
                .ToArray();
            files.Should().Equal("Data/mod.txt", "Data/vanilla.txt");
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task ADirectoryLargerThanOneReadDirPageListsEveryEntryOnce()
    {
        RequiresFuse.SkipUnlessAvailable();
        // Long names make each record big enough that the listing needs several readdirplus round trips.
        var staged = Enumerable.Range(0, 1500)
            .Select(i => ($"Data/many/{new string('n', 90)}-{i:D5}.txt", Bytes("x")))
            .ToArray();
        var mounted = await MountTree(staged);
        try
        {
            var names = Directory.EnumerateFiles(Path.Combine(mounted.MountPoint.ToString(), "Data", "many")).Select(Path.GetFileName).ToArray();
            names.Should().HaveCount(1500).And.OnlyHaveUniqueItems();
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task LookupFoldsCaseAndMissingNamesAreAbsent()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            var root = mounted.MountPoint.ToString();
            File.ReadAllText(Path.Combine(root, "Data", "MOD.TXT")).Should().Be("staged");
            File.Exists(Path.Combine(root, "Data", "missing")).Should().BeFalse();
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task AFileLargerThanOneReadPageReadsBackEqual()
    {
        RequiresFuse.SkipUnlessAvailable();
        var big = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(big);
        var mounted = await MountTree(("Data/big.bin", big));
        try
        {
            File.ReadAllBytes(Path.Combine(mounted.MountPoint.ToString(), "Data", "big.bin")).Should().Equal(big);
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task ANewFileLandsInStagingAndReadsBack()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            var path = Path.Combine(mounted.MountPoint.ToString(), "Data", "new.txt");
            File.WriteAllText(path, "written through the mount");

            File.ReadAllText(path).Should().Be("written through the mount");
            File.ReadAllText(mounted.Staging.Root.Combine("Data/new.txt").ToString()).Should().Be("written through the mount");
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task CopyUpShowsTheNewSizeAfterTheWrite()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            var path = Path.Combine(mounted.MountPoint.ToString(), "Data", "vanilla.txt");
            new FileInfo(path).Length.Should().Be(7);

            File.WriteAllText(path, "a much longer replacement");

            new FileInfo(path).Length.Should().Be(25);
            File.ReadAllText(path).Should().Be("a much longer replacement");
            File.ReadAllText(mounted.Backing.Combine("Data/vanilla.txt").ToString()).Should().Be("vanilla", "the game's own directory is never written");
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task MountInfoShowsTheMountOnlyWhileItIsUp()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            File.ReadLines(MountInfo).Count(l => l.Contains($"/{mounted.MountPoint.FileName} ", StringComparison.Ordinal) && l.Contains(" - fuse.silvermod ", StringComparison.Ordinal))
                .Should().Be(1);
            IsMounted(mounted.MountPoint).Should().BeTrue();
        }
        finally { await Unmount(mounted); }

        IsMounted(mounted.MountPoint).Should().BeFalse();
        mounted.Mount.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task InitNegotiatesTheLargeWriteSizeWithTheLibraryTheAbiCheckAccepted()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        try
        {
            // A read forces the kernel handshake to have completed.
            Directory.EnumerateFileSystemEntries(mounted.MountPoint.ToString()).Should().NotBeEmpty();

            var negotiated = ((LibFuseLowLevelMount)mounted.Mount).Negotiated;
            negotiated.Should().NotBeNull();
            negotiated!.Soname.Should().Be(FuseAbi.SupportedSoname);
            negotiated.FuseVersion.Should().BeGreaterThanOrEqualTo(318);
            negotiated.MaxWrite.Should().Be(1u << 20);
            output.WriteLine(negotiated.ToString());
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task ARefusedUnmountLeavesTheMountServing()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        var path = Path.Combine(mounted.MountPoint.ToString(), "Data", "mod.txt");
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var unmount = () => mounted.Mount.Unmount(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                await unmount.Should().ThrowAsync<IOException>("fusermount3 refuses a mount with a file open");

                // A refused unmount must not leave a mount nobody answers: every later request would hang.
                var text = await Task.Run(() => File.ReadAllText(path)).WaitAsync(TimeSpan.FromSeconds(5));
                text.Should().Be("staged");
            }
        }
        finally { await Unmount(mounted); }
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task UnmountWhileAFileIsOpenCompletesWithLazyFallback()
    {
        RequiresFuse.SkipUnlessAvailable();
        var mounted = await MountTree(("Data/mod.txt", Bytes("staged")));
        var open = new FileStream(Path.Combine(mounted.MountPoint.ToString(), "Data", "mod.txt"), FileMode.Open, FileAccess.Read);
        try
        {
            // The plain unmount is refused while the file is open; disposing falls back to a lazy one.
            await mounted.Mount.DisposeAsync();
            IsMounted(mounted.MountPoint).Should().BeFalse("a lazy unmount detaches the mount while a file is open");
        }
        finally
        {
            open.Dispose();
        }

        // Closing the last file lets the kernel drop the connection and the session loop exit.
        await mounted.Mount.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
