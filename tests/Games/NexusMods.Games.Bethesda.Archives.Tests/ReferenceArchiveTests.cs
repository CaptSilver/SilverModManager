namespace NexusMods.Games.Bethesda.Archives.Tests;

/// <summary>
/// Opens Wabbajack's own sample archives (Wabbajack.Compression.BSA.Test/TestFiles) and extracts every
/// entry. Runs only when WABBAJACK_BSA_TESTFILES names the directory holding them; the files are too big
/// to keep in the repository.
/// </summary>
public class ReferenceArchiveTests
{
    private const string DirectoryVariable = "WABBAJACK_BSA_TESTFILES";

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

    [Theory]
    [MemberData(nameof(Files))]
    public async Task EveryEntryShouldExtractToItsRecordedSize(string name)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(directory), $"{DirectoryVariable} is not set");

        await using var reader = await BethesdaArchive.Open(
            File.OpenRead(Path.Combine(directory!, name)), CancellationToken.None);

        reader.Entries.Should().NotBeEmpty();
        foreach (var entry in reader.Entries)
        {
            entry.Path.Depth.Should().BeGreaterThan(0);
            var output = new MemoryStream();
            await entry.CopyToAsync(output, CancellationToken.None);
            ((ulong)output.Length).Should().Be(entry.Size, $"{name}: {entry.Path}");
        }
    }
}
