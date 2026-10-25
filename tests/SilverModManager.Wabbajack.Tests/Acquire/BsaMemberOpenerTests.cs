using FluentAssertions;
using SilverModManager.Games.Bethesda.Archives;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class BsaMemberOpenerTests : IDisposable
{
    private readonly TemporaryFileManager _temp = new(
        FileSystem.Shared,
        FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine($"Wabbajack.Tests-{Guid.NewGuid()}")));

    private readonly BsaMemberOpener _opener = new();

    public void Dispose() => _temp.Dispose();

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private static async Task<byte[]> ReadAll(Stream? stream)
    {
        stream.Should().NotBeNull();
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Theory]
    [InlineData("pack.bsa", true)]
    [InlineData("Data/PACK.BA2", true)]
    [InlineData("pack.zip", false)]
    [InlineData("pack.bsa.txt", false)]
    public void OnlyBethesdaArchivesCanBeOpened(string name, bool expected)
        => _opener.CanOpen(P(name)).Should().Be(expected);

    [Fact]
    public async Task AMemberIsFoundByItsPathWhateverTheCase()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        var member = await _opener.Open(new MemoryStream(archive), P("Meshes\\FIRST.nif"), CancellationToken.None);

        (await ReadAll(member)).Should().Equal(TestArchives.FirstMemberData);
    }

    [Fact]
    public async Task AMemberTheArchiveLacksIsNull()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        (await _opener.Open(new MemoryStream(archive), P("meshes/absent.nif"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnArchiveStreamThatCannotSeekIsStillRead()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        var member = await _opener.Open(new ForwardOnlyStream(new MemoryStream(archive)), TestArchives.SecondMemberPath, CancellationToken.None);

        (await ReadAll(member)).Should().Equal(TestArchives.SecondMemberData);
    }

    [Fact]
    public async Task BytesThatAreNotAnArchiveAreAMemberNotFoundNamingTheMember()
    {
        var failure = await Record.ExceptionAsync(() => _opener.Open(new MemoryStream(new byte[100]), P("meshes/a.nif"), CancellationToken.None));

        failure.Should().BeOfType<MemberNotFoundException>().Which.Message.Should().Contain("meshes/a.nif");
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
