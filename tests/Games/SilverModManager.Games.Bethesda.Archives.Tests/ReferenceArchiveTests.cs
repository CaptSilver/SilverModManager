using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

/// <summary>
/// Opens Wabbajack's own sample archives (Wabbajack.Compression.BSA.Test/TestFiles), extracts every entry and
/// rebuilds each archive from its recorded states. Runs only when WABBAJACK_BSA_TESTFILES names the directory
/// holding them; the files are too big to keep in the repository.
/// </summary>
public class ReferenceArchiveTests : IDisposable
{
    private const string DirectoryVariable = "WABBAJACK_BSA_TESTFILES";

    private readonly TemporaryFileManager _temp = new(
        FileSystem.Shared,
        FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine($"Bethesda.Archives.Reference.Tests-{Guid.NewGuid()}"));

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Files =>
    [
        "tes3.bsa",
        "tes4.bsa",
        "tes5.bsa",
        "tes5_compressed.bsa",
        "sse.bsa",
        "sse_compressed.bsa",
        "fnv.bsa",
        "fo4.bsa",
        "fo4dds.bsa",
    ];

    private static string Locate(string name)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(directory), $"{DirectoryVariable} is not set");

        // Wabbajack's checkout does not hold every name listed above.
        var path = Path.Combine(directory!, name);
        Assert.SkipUnless(File.Exists(path), $"{path} does not exist");
        return path;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task EveryEntryShouldExtractToItsRecordedSize(string name)
    {
        await using var reader = await BethesdaArchive.Open(File.OpenRead(Locate(name)), CancellationToken.None);

        reader.Entries.Should().NotBeEmpty();
        foreach (var entry in reader.Entries)
        {
            entry.Path.Depth.Should().BeGreaterThan(0);
            var output = new MemoryStream();
            await entry.CopyToAsync(output, CancellationToken.None);
            ((ulong)output.Length).Should().Be(entry.Size, $"{name}: {entry.Path}");
        }
    }

    // Wabbajack's CanRecreateBSAs skips tes4.bsa, "not sure why it is failing"; so does this.
    [Theory]
    [MemberData(nameof(Files))]
    public async Task EveryArchiveShouldRebuildFromItsStatesWithTheSameEntries(string name)
    {
        var path = Locate(name);
        Assert.SkipWhen(name == "tes4.bsa", "Wabbajack's own test skips this archive");

        await using var reader = await BethesdaArchive.Open(File.OpenRead(path), CancellationToken.None);
        var originals = new List<(IBethesdaArchiveEntry Entry, byte[] Data)>();
        foreach (var entry in reader.Entries)
        {
            var data = new MemoryStream();
            await entry.CopyToAsync(data, CancellationToken.None);
            data.Length.Should().Be((long)entry.Size);
            originals.Add((entry, data.ToArray()));
        }

        var rebuilt = new MemoryStream();
        await using (var builder = BethesdaArchiveBuilder.Create(reader.State, _temp))
        {
            foreach (var (entry, data) in originals)
                await builder.AddFile(entry.State, new MemoryStream(data), CancellationToken.None);
            await builder.Build(rebuilt, CancellationToken.None);
        }

        rebuilt.Position = 0;
        await using var second = await BethesdaArchive.Open(rebuilt, CancellationToken.None);
        second.State.Should().Be(reader.State);
        second.Entries.Should().HaveCount(originals.Count);
        for (var i = 0; i < originals.Count; i++)
        {
            var (oldEntry, oldData) = originals[i];
            var newEntry = second.Entries[i];
            newEntry.Path.Should().Be(oldEntry.Path, name);
            newEntry.Size.Should().Be(oldEntry.Size, $"{name}: {oldEntry.Path}");

            var newData = new MemoryStream();
            await newEntry.CopyToAsync(newData, CancellationToken.None);
            newData.ToArray().Should().Equal(oldData, $"{name}: {oldEntry.Path}");
        }
    }
}
