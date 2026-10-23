using FluentAssertions;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class GoldenFilesTests
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Formats", "Golden");

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Folder, name));

    [Fact]
    public void TheGoldenFolderIsCopiedToTheOutputFolder()
    {
        Directory.Exists(Folder).Should().BeTrue("the test project copies Formats/Golden beside the test assembly");
    }

    [Fact]
    public void EveryBlobHasTheCallThatProducedItRecordedBesideIt()
    {
        var blobs = Directory.EnumerateFiles(Folder).Where(f => !f.EndsWith(".erm.txt", StringComparison.Ordinal)).ToArray();
        blobs.Should().NotBeEmpty();
        foreach (var blob in blobs)
            File.Exists(blob + ".erm.txt").Should().BeTrue($"{Path.GetFileName(blob)} is a golden blob");
    }

    [Fact]
    public void EveryRecordedCallHasItsBlob()
    {
        var notes = Directory.EnumerateFiles(Folder, "*.erm.txt").ToArray();
        notes.Should().NotBeEmpty();
        foreach (var note in notes)
            File.Exists(note[..^".erm.txt".Length]).Should().BeTrue($"{Path.GetFileName(note)} describes a blob");
    }
}
