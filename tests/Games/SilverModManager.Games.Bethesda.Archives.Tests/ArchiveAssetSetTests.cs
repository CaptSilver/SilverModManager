using static SilverModManager.Games.Bethesda.Archives.Tests.TestArchives;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

// Follows the normalisation and overlap rules of libloot's archive module (archive/mod.rs, bsa.rs, ba2.rs).
public class ArchiveAssetSetTests
{
    [Fact]
    public void PathsDifferingOnlyInCaseAndSeparatorsAreTheSameAsset()
    {
        var set = ArchiveAssetSet.FromPaths(["Meshes/Armor/Iron.NIF", "meshes\\armor\\iron.nif", "\\meshes\\armor\\iron.nif\\"]);

        set.Count.Should().Be(1);
    }

    [Fact]
    public void OnlyAsciiLettersAreFolded()
    {
        // libloot lowercases single bytes below 128 and leaves the rest alone.
        ArchiveAssetSet.FromPaths(["textures\\Ä.dds"]).Overlaps(ArchiveAssetSet.FromPaths(["textures\\ä.dds"])).Should().BeFalse();
        ArchiveAssetSet.FromPaths(["textures\\Ä.dds"]).Overlaps(ArchiveAssetSet.FromPaths(["TEXTURES\\Ä.dds"])).Should().BeTrue();
    }

    [Fact]
    public void TheSameFileInTheSameFolderOverlaps()
    {
        var a = ArchiveAssetSet.FromPaths(["meshes/a.nif", "meshes/b.nif"]);
        var b = ArchiveAssetSet.FromPaths(["meshes/b.nif", "meshes/c.nif"]);

        a.Overlaps(b).Should().BeTrue();
        b.Overlaps(a).Should().BeTrue();
    }

    [Fact]
    public void TheSameFileNameInDifferentFoldersDoesNotOverlap()
    {
        ArchiveAssetSet.FromPaths(["meshes/a.nif"]).Overlaps(ArchiveAssetSet.FromPaths(["meshes/sub/a.nif"])).Should().BeFalse();
        ArchiveAssetSet.FromPaths(["a.nif"]).Overlaps(ArchiveAssetSet.FromPaths([".\\a.nif"])).Should().BeFalse();
    }

    [Fact]
    public void AnEmptySetOverlapsNothing()
    {
        var some = ArchiveAssetSet.FromPaths(["meshes/a.nif"]);

        ArchiveAssetSet.Empty.Count.Should().Be(0);
        ArchiveAssetSet.Empty.Overlaps(some).Should().BeFalse();
        some.Overlaps(ArchiveAssetSet.Empty).Should().BeFalse();
        ArchiveAssetSet.Empty.Overlaps(ArchiveAssetSet.Empty).Should().BeFalse();
    }

    [Fact]
    public void UnionCountsAnAssetOnceHoweverManyArchivesHoldIt()
    {
        var first = ArchiveAssetSet.FromPaths(["meshes/a.nif", "meshes/b.nif"]);
        var second = ArchiveAssetSet.FromPaths(["MESHES/B.NIF", "meshes/c.nif"]);

        var union = ArchiveAssetSet.Union([first, second]);

        union.Count.Should().Be(3);
        union.Overlaps(ArchiveAssetSet.FromPaths(["meshes/c.nif"])).Should().BeTrue();
    }

    [Fact]
    public async Task ReadListsTheFilesOfASkyrimArchive()
    {
        var bytes = Tes5(104, FolderAndFileNames,
            new Tes5Entry("Meshes\\Armor", "Iron.nif", Pattern(10, 1), false),
            new Tes5Entry("Textures", "Iron.dds", Pattern(10, 2), false));

        var set = await ArchiveAssetSet.Read(new MemoryStream(bytes), CancellationToken.None);

        set.Count.Should().Be(2);
        set.Overlaps(ArchiveAssetSet.FromPaths(["meshes/armor/iron.nif"])).Should().BeTrue();
        set.Overlaps(ArchiveAssetSet.FromPaths(["meshes/iron.nif"])).Should().BeFalse();
    }

    [Fact]
    public async Task ReadListsTheFilesOfAFalloutFourArchive()
    {
        var bytes = Ba2General(1, 0, new Ba2Entry("Meshes\\a.nif", Pattern(20, 1), false), new Ba2Entry("Sound\\b.xwm", Pattern(20, 2), true));

        var set = await ArchiveAssetSet.Read(new MemoryStream(bytes), CancellationToken.None);

        set.Count.Should().Be(2);
        set.Overlaps(ArchiveAssetSet.FromPaths(["sound/b.xwm"])).Should().BeTrue();
    }

    [Fact]
    public async Task ReadSpoolsAStreamThatCannotSeek()
    {
        var bytes = Tes5(104, FolderAndFileNames, new Tes5Entry("Meshes", "a.nif", Pattern(10, 1), false));

        var set = await ArchiveAssetSet.Read(new ForwardOnlyStream(bytes), CancellationToken.None);

        set.Count.Should().Be(1);
    }

    [Fact]
    public async Task ReadRefusesBytesThatAreNotAnArchive()
    {
        var act = () => ArchiveAssetSet.Read(new MemoryStream("not an archive at all"u8.ToArray()), CancellationToken.None);

        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
