using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Deployment.Vfs.Testing;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Fuse.Tests.Backend;

/// <summary>
/// What finishing a mount does with the session's staged writes, against the fake kernel so no mount is needed.
/// </summary>
public class FuseMountTests : IDisposable
{
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(0xABC);
    private static readonly GamePath Root = new(LocationId.Game, "Data");

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-mount-tests-{Guid.NewGuid():N}");

    private readonly RecordingSink _sink = new();

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private sealed class Mounted(FuseMount mount, VfsSession session, DirectoryHandle backing, IFuseMount kernelMount)
    {
        public FuseMount Mount => mount;
        public VfsSession Session => session;
        public DirectoryHandle Backing => backing;
        public IFuseMount KernelMount => kernelMount;
    }

    private async Task<Mounted> NewMount(Func<IFuseMount, IFuseMount>? wrap = null, TimeSpan? completionTimeout = null)
    {
        var backingPath = _directory.Combine("game");
        backingPath.CreateDirectory();
        var backing = DirectoryHandle.Open(backingPath);
        var staging = new StagingDirectory(_directory.Combine("staging"), Installation);
        var tree = VfsTree.Build(backing, [], [], staging, VfsCachePolicy.Default);
        var promoter = new StagingPromoter(staging, Root, _sink, new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance, Size.From(new VfsSettings().PromotionSizeCapBytes));
        var session = new VfsSession(tree, staging, promoter, Target);

        var kernel = new FakeFuseKernel();
        var kernelMount = await kernel.Mount(tree, FuseMountOptions.Default(_directory.Combine("mount")), CancellationToken.None);
        var mount = new FuseMount(wrap?.Invoke(kernelMount) ?? kernelMount, session, backing, Root, completionTimeout ?? TimeSpan.FromSeconds(30), NullLogger.Instance);
        return new Mounted(mount, session, backing, kernelMount);
    }

    private static void Stage(VfsSession session, string path, string text)
    {
        var file = session.Staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task FinishPromotesIntoTheTargetLoadoutOnceTheSessionCompletes()
    {
        var mounted = await NewMount();
        Stage(mounted.Session, "new.esp", "bytes");

        var result = await mounted.Mount.Finish(CancellationToken.None);

        result.Status.Should().Be(MountFinishStatus.Promoted);
        result.Files.Select(f => f.ToString()).Should().Equal("new.esp");
        var record = _sink.Records.Should().ContainSingle().Subject;
        record.Loadout.Should().Be(Target);
        record.Root.Should().Be(Root);
        record.Files.Select(f => f.Path.ToString()).Should().Equal("new.esp");
    }

    [Fact]
    public async Task FinishWithNothingStagedHasNothingToDo()
    {
        var mounted = await NewMount();

        var result = await mounted.Mount.Finish(CancellationToken.None);

        result.Status.Should().Be(MountFinishStatus.NothingToDo);
        _sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishWithACompletionThatNeverEndsReportsStillInUseAndPromotesNothing()
    {
        var mounted = await NewMount(real => new NeverCompletes(real), completionTimeout: TimeSpan.FromMilliseconds(50));
        Stage(mounted.Session, "new.esp", "bytes");

        var result = await mounted.Mount.Finish(CancellationToken.None);

        result.Status.Should().Be(MountFinishStatus.StillInUse);
        _sink.Records.Should().BeEmpty("a file the game may still be writing is not promoted");
        mounted.Session.Staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp")).FileExists.Should().BeTrue("the next recovery promotes it");
    }

    [Fact]
    public async Task FinishUnmountsBeforeItPromotes()
    {
        var order = new List<string>();
        var mounted = await NewMount(real => new RecordingMount(real, order));
        _sink.OnRecord = () => order.Add("record");
        Stage(mounted.Session, "new.esp", "bytes");

        await mounted.Mount.Finish(CancellationToken.None);

        order.Should().Equal("unmount", "dispose", "record");
    }

    [Fact]
    public async Task FinishClosesTheBackingHandle()
    {
        var mounted = await NewMount();

        await mounted.Mount.Finish(CancellationToken.None);

        mounted.Backing.Handle.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task FinishClosesTheBackingHandleWhenTheSessionStillRuns()
    {
        var mounted = await NewMount(real => new NeverCompletes(real), completionTimeout: TimeSpan.FromMilliseconds(20));

        await mounted.Mount.Finish(CancellationToken.None);

        // The tree may still be answering the lingering process, but it reads through the handle only while it is open;
        // the next recovery promotes from staging, which does not need it.
        mounted.Backing.Handle.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task TheMountReportsItsRootAndMountPoint()
    {
        var mounted = await NewMount();

        mounted.Mount.Root.Should().Be(Root);
        mounted.Mount.MountPoint.Should().Be(mounted.KernelMount.MountPoint);
    }

    private class WrappedMount(IFuseMount inner) : IFuseMount
    {
        public AbsolutePath MountPoint => inner.MountPoint;
        public virtual Task Completion => inner.Completion;
        public void InvalidateEntry(ulong parent, string name) => inner.InvalidateEntry(parent, name);
        public void InvalidateInode(ulong ino) => inner.InvalidateInode(ino);
        public virtual Task Unmount(CancellationToken token) => inner.Unmount(token);
        public virtual ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NeverCompletes(IFuseMount inner) : WrappedMount(inner)
    {
        public override Task Completion { get; } = new TaskCompletionSource().Task;
    }

    private sealed class RecordingMount(IFuseMount inner, List<string> order) : WrappedMount(inner)
    {
        public override Task Unmount(CancellationToken token)
        {
            order.Add("unmount");
            return base.Unmount(token);
        }

        public override ValueTask DisposeAsync()
        {
            order.Add("dispose");
            return base.DisposeAsync();
        }
    }

    private sealed class RecordingSink : IPromotionSink
    {
        public List<(LoadoutId Loadout, GamePath Root, IReadOnlyList<PromotedFile> Files, IReadOnlyList<RelativePath> Deleted)> Records { get; } = [];
        public Action? OnRecord { get; set; }

        public Task Backup(IReadOnlyList<(FileIdentity Identity, Size Size, IStreamFactory Stream)> files, CancellationToken token = default) => Task.CompletedTask;

        public Task Record(LoadoutId loadout, GamePath root, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
        {
            OnRecord?.Invoke();
            Records.Add((loadout, root, files, deleted));
            return Task.CompletedTask;
        }
    }
}
