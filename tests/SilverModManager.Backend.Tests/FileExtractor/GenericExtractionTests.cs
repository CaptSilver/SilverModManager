using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class GenericExtractionTests : AFileExtractorTest
{
    // Only the archives the expected hashes below describe. A name that goes missing
    // fails the test instead of silently dropping out of an enumerated directory.
    public static TheoryData<string> Archives => ["data_7zip_lzma2.7z", "data_zip_lzma.zip"];

    [Theory]
    [MemberData(nameof(Archives))]
    public async Task CanExtractAll(string archiveName)
    {
        var path = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(archiveName);

        await using var tempFolder = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(path, tempFolder, TestContext.Current.CancellationToken);

        var actual = await tempFolder.Path.EnumerateFiles()
            .ToAsyncEnumerable()
            .SelectAwait(async f => (f.RelativeTo(tempFolder.Path), await f.XxHash3Async()))
            .ToArrayAsync(TestContext.Current.CancellationToken);

        (RelativePath, Hash)[] expected = [
            ("deepFolder/deepFolder2/deepFolder3/deepFolder4/deepFile.txt", (Hash)0x3F0AB4D495E35A9A),
            ("folder1/folder1file.txt", (Hash)0x8520436F06348939),
            ("rootFile.txt", (Hash)0x818A82701BC1CC30),
        ];

        actual.Should().BeEquivalentTo(expected);
    }
}
