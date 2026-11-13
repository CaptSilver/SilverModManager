using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ManifestNamerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manifest-namer-" + Guid.NewGuid().ToString("N"));

    private AbsolutePath WriteAbout(string packageId, DateTime? written = null)
    {
        var folder = Path.Combine(_root, "item");
        Directory.CreateDirectory(Path.Combine(folder, "About"));
        var file = Path.Combine(folder, "About", "About.xml");
        File.WriteAllText(file, $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
        if (written is { } time) File.SetLastWriteTimeUtc(file, time);
        return FileSystem.Shared.FromUnsanitizedFullPath(folder);
    }

    [Fact]
    public void AFolderIsNamedByItsManifest()
    {
        new ManifestNamer(new AboutXmlManifestReader()).Names(WriteAbout("A.B")).Should().Equal("a.b");
    }

    [Fact]
    public void AFolderWithoutItsManifestHasNoName()
    {
        Directory.CreateDirectory(_root);

        new ManifestNamer(new AboutXmlManifestReader()).Names(FileSystem.Shared.FromUnsanitizedFullPath(_root)).Should().BeEmpty();
    }

    [Fact]
    public void ASecondFindOverUnchangedFoldersReadsNoFile()
    {
        var namer = new ManifestNamer(new AboutXmlManifestReader());
        var folder = WriteAbout("A.B");

        namer.Names(folder);
        namer.Names(folder).Should().Equal("a.b");

        namer.FileReads.Should().Be(1);
    }

    [Fact]
    public void AChangedManifestIsReadAgain()
    {
        var namer = new ManifestNamer(new AboutXmlManifestReader());
        var folder = WriteAbout("A.B", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        namer.Names(folder);

        WriteAbout("C.D", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        namer.Names(folder).Should().Equal("c.d");
        namer.FileReads.Should().Be(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
