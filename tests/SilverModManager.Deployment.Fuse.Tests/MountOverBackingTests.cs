using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Fuse.Tests;

/// <summary>
/// The deployment mounts over the game's own data folder, so everything the daemon reads from the real
/// folder has to go through a descriptor opened before the mount: by path it would reach itself.
/// </summary>
[Collection(MountingTests.Name)]
public class MountOverBackingTests : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-over-backing-{Guid.NewGuid():N}");

    private string? _mountPoint;

    public void Dispose()
    {
        // A failed test must not leave a mount behind in the temp folder.
        if (_mountPoint is not null) ForceUnmount(_mountPoint);
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }

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

    // A call into the mounted folder that deadlocks on itself must fail the test instead of hanging it.
    private static async Task<T> Bounded<T>(Func<T> call)
    {
        var task = Task.Run(call);
        return await task.WaitAsync(CallTimeout, TestContext.Current.CancellationToken);
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task MountOverBackingDirectory_ServesWritesAndRenamesThroughTheDescriptor()
    {
        RequiresFuse.SkipUnlessAvailable();
        var data = _directory.Combine("game/Data");
        data.CreateDirectory();
        await data.Combine("vanilla.txt").WriteAllBytesAsync("vanilla"u8.ToArray());
        var loose = _directory.Combine("loose/mod");
        loose.Parent.CreateDirectory();
        await loose.WriteAllBytesAsync("mod"u8.ToArray());
        var staging = new StagingDirectory(_directory.Combine("staging"), GameInstallMetadataId.From(0xABC));

        using var backing = DirectoryHandle.Open(data);
        var entries = new BackingScanner().Scan(backing);
        var source = new VfsFileSource(RelativePath.FromUnsanitizedInput("mod.txt"), VfsSourceKind.Staged, loose, Size.From(3), FileIdentity.XxHash3(Hash.From(2)));
        var tree = VfsTree.Build(backing, entries, [source], staging, VfsCachePolicy.Default);

        _mountPoint = data.ToString();
        var kernel = new LibFuseLowLevelKernel(NullLogger<LibFuseLowLevelKernel>.Instance, new FusermountUnmounter(new CliFusermountRunner()));
        var mount = await kernel.Mount(tree, FuseMountOptions.Default(data), TestContext.Current.CancellationToken);
        try
        {
            (await Bounded(() => File.ReadAllText(data.Combine("vanilla.txt").ToString()))).Should().Be("vanilla");
            (await Bounded(() => File.ReadAllText(data.Combine("mod.txt").ToString()))).Should().Be("mod");

            await Bounded(() => { File.AppendAllText(data.Combine("vanilla.txt").ToString(), "+"); return 0; });
            (await Bounded(() => File.ReadAllText(data.Combine("vanilla.txt").ToString()))).Should().Be("vanilla+");

            await Bounded(() => { File.Move(data.Combine("mod.txt").ToString(), data.Combine("renamed.txt").ToString()); return 0; });
            (await Bounded(() => File.ReadAllText(data.Combine("renamed.txt").ToString()))).Should().Be("mod");
        }
        finally
        {
            await mount.Unmount(TestContext.Current.CancellationToken);
        }

        staging.Scan().Select(e => e.Path.ToString()).Should().BeEquivalentTo("vanilla.txt", "renamed.txt");
        File.ReadAllText(data.Combine("vanilla.txt").ToString()).Should().Be("vanilla", "the real folder underneath is never written");
    }
}
