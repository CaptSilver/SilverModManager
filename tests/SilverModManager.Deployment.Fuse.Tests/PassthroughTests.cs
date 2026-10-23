using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Deployment.Fuse.Kernel;
using NexusMods.Deployment.Fuse.Native;
using NexusMods.Deployment.Vfs;
using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Tests;

/// <summary>
/// The decision to hand a read-only open to the kernel's passthrough, against a registrar that answers as
/// scripted. Nothing here mounts anything: the registrar is the only thing that talks to the kernel.
/// </summary>
public class PassthroughTests : IDisposable
{
    private const int EPERM = 1;
    private static readonly IntPtr Request = new(0x1000);

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-passthrough-{Guid.NewGuid():N}");

    private readonly List<VfsOpenFile> _files = [];

    public void Dispose()
    {
        foreach (var file in _files) file.Handle.Dispose();
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }

    private VfsOpenFile OpenFile(bool writable = false, bool directIo = false)
    {
        _directory.CreateDirectory();
        var path = _directory.Combine($"{Guid.NewGuid():N}.bin");
        File.WriteAllText(path.ToString(), "bytes");
        var file = new VfsOpenFile
        {
            Ino = 2,
            Handle = File.OpenHandle(path.ToString(), FileMode.Open, FileAccess.Read),
            RealPath = path,
            Writable = writable,
            DirectIo = directIo,
        };
        _files.Add(file);
        return file;
    }

    private static KernelPassthrough Enabled(FakeRegistrar registrar, ILogger? logger = null)
    {
        var passthrough = new KernelPassthrough(registrar, logger ?? NullLogger.Instance);
        passthrough.Enable();
        return passthrough;
    }

    [Fact]
    public void ARefusedRegistrationIsTriedOnceThenReadsGoThroughTheDaemon()
    {
        var registrar = new FakeRegistrar(id: 0, errno: EPERM);
        var logger = new RecordingLogger();
        var passthrough = Enabled(registrar, logger);

        var first = new FuseFileInfo();
        var second = new FuseFileInfo();
        passthrough.TryAttach(Request, 1, OpenFile(), ref first).Should().BeFalse();
        passthrough.TryAttach(Request, 2, OpenFile(), ref second).Should().BeFalse();

        registrar.Registered.Should().HaveCount(1, "a refusal for lack of privilege will not change on the next open");
        first.BackingId.Should().Be(0);
        second.BackingId.Should().Be(0);
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("kernel passthrough refused: EPERM; reads go through the daemon");
    }

    [Fact]
    public void AnAcceptedRegistrationSetsTheBackingIdOnTheReply()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);
        var info = new FuseFileInfo();

        passthrough.TryAttach(Request, 1, OpenFile(), ref info).Should().BeTrue();

        info.BackingId.Should().Be(7, "libfuse turns a positive backing id into the passthrough open flag");
        registrar.Registered.Should().HaveCount(1);
    }

    [Fact]
    public void EveryOpenOfAnAcceptingKernelRegistersItsOwnFile()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);
        var first = new FuseFileInfo();
        var second = new FuseFileInfo();

        passthrough.TryAttach(Request, 1, OpenFile(), ref first);
        passthrough.TryAttach(Request, 2, OpenFile(), ref second);

        registrar.Registered.Should().HaveCount(2);
    }

    [Fact]
    public void ReleaseClosesTheBackingId()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);
        var info = new FuseFileInfo();
        passthrough.TryAttach(Request, 11, OpenFile(), ref info);

        passthrough.Release(Request, 11);

        registrar.Closed.Should().Equal(7);
    }

    [Fact]
    public void ReleaseOfAHandleThatNeverGotABackingIdClosesNothing()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);

        passthrough.Release(Request, 99);

        registrar.Closed.Should().BeEmpty();
    }

    [Fact]
    public void ReleaseWithNoRequestForgetsTheIdWithoutTalkingToTheKernel()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);
        var info = new FuseFileInfo();
        passthrough.TryAttach(Request, 11, OpenFile(), ref info);

        passthrough.Release(IntPtr.Zero, 11);
        passthrough.Release(Request, 11);

        registrar.Closed.Should().BeEmpty("closing a backing id needs a request, and the kernel drops them with the connection");
    }

    [Fact]
    public void NothingIsRegisteredUntilInitAskedForPassthrough()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = new KernelPassthrough(registrar, NullLogger.Instance);
        var info = new FuseFileInfo();

        passthrough.TryAttach(Request, 1, OpenFile(), ref info).Should().BeFalse();

        registrar.Registered.Should().BeEmpty();
    }

    [Fact]
    public void WritableAndDirectIoFilesStayWithTheDaemon()
    {
        var registrar = new FakeRegistrar(id: 7, errno: 0);
        var passthrough = Enabled(registrar);
        var info = new FuseFileInfo();

        passthrough.TryAttach(Request, 1, OpenFile(writable: true), ref info).Should().BeFalse("the tree sees every write to copy or stage it");
        passthrough.TryAttach(Request, 2, OpenFile(directIo: true), ref info).Should().BeFalse();

        registrar.Registered.Should().BeEmpty();
    }

    [Fact]
    public void PassthroughIsNotRequestedWithoutCapSysAdmin()
    {
        PassthroughPolicy.HasCapSysAdmin("Name:\tx\nCapEff:\t0000000000000000\nCapBnd:\t000001ffffffffff\n").Should().BeFalse();
        PassthroughPolicy.HasCapSysAdmin("Name:\tx\nCapEff:\t0000000000200000\n").Should().BeTrue("bit 21 is CAP_SYS_ADMIN");
        PassthroughPolicy.HasCapSysAdmin("CapEff:\t000001ffffffffff\n").Should().BeTrue();
        PassthroughPolicy.HasCapSysAdmin("CapEff:\t00000000ffdfffff\n").Should().BeFalse("every other capability set, bit 21 clear");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name:\tx\n")]
    [InlineData("CapEff:\tnot-hex\n")]
    public void StatusTextWithoutAReadableCapEffMeansNoCapability(string status)
    {
        PassthroughPolicy.HasCapSysAdmin(status).Should().BeFalse();
    }

    internal sealed class FakeRegistrar(int id, int errno) : IPassthroughRegistrar
    {
        public List<int> Registered { get; } = [];
        public List<int> Closed { get; } = [];

        public int TryRegister(IntPtr req, int fd, out int error)
        {
            Registered.Add(fd);
            error = errno;
            return id;
        }

        public void Close(IntPtr req, int backingId) => Closed.Add(backingId);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logLevel.Should().Be(LogLevel.Information);
            Messages.Add(formatter(state, exception));
        }
    }
}
