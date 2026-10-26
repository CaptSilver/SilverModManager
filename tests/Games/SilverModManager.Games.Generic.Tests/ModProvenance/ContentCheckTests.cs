using FluentAssertions;
using SilverModManager.Games.Generic.ModProvenance;

namespace SilverModManager.Games.Generic.Tests.ModProvenance;

public class ContentCheckTests : IDisposable
{
    private readonly Tree _tree = new();

    public void Dispose() => _tree.Dispose();

    private IReadOnlyList<LocalFile> Folder() => ContentCheck.LocalFiles(_tree.Path_("mod"));

    [Fact]
    public void LocalFilesAreListedWithRelativePathsAndSizes()
    {
        _tree.Write("mod/NoWeight.dll", "12345");
        _tree.Write("mod/sub/x.ini", "12");

        Folder().Should().BeEquivalentTo([new LocalFile("NoWeight.dll", 5), new LocalFile("sub/x.ini", 2)]);
    }

    [Fact]
    public void AMissingFolderHasNoFiles()
    {
        Folder().Should().BeEmpty();
    }

    [Fact]
    public void TheSameFilesWithTheSameSizesAreAccepted()
    {
        _tree.Write("mod/NoWeight.dll", "12345");
        _tree.Write("mod/sub/x.ini", "12");

        var listing = new[] { new ArchiveEntry("NoWeight.dll", 5), new ArchiveEntry("sub/x.ini", 2), new ArchiveEntry("readme.txt", 100) };

        ContentCheck.Accepts(Folder(), listing).Should().BeTrue();
    }

    [Fact]
    public void AWrapperFolderInTheArchiveDoesNotMatter()
    {
        _tree.Write("mod/NoWeight.dll", "12345");

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("NoWeight/NoWeight.dll", 5)]).Should().BeTrue();
    }

    [Fact]
    public void TheSameNameWithAnotherSizeIsRejected()
    {
        _tree.Write("mod/NoWeight.dll", "12345");

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("NoWeight.dll", 4096)]).Should().BeFalse();
    }

    [Fact]
    public void AFileTheArchiveLacksIsRejected()
    {
        _tree.Write("mod/NoWeight.dll", "12345");
        _tree.Write("mod/extra.dll", "1");

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("NoWeight.dll", 5)]).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyFolderIsNeverAccepted()
    {
        Directory.CreateDirectory(_tree.Path_("mod"));

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("a", 1)]).Should().BeFalse();
    }

    [Fact]
    public void TheManagersOwnMetaFileIsNotComparedAndNamesIgnoreCase()
    {
        _tree.Write("mod/meta.ini", "[General]");
        _tree.Write("mod/NoWeight.DLL", "12345");

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("noweight.dll", 5)]).Should().BeTrue();
    }

    [Fact]
    public void AFolderOfOnlyTheManagersMetaFileIsNeverAccepted()
    {
        _tree.Write("mod/meta.ini", "[General]");

        ContentCheck.Accepts(Folder(), [new ArchiveEntry("meta.ini", 9)]).Should().BeFalse();
    }
}
