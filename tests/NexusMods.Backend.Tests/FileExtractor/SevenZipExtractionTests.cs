using FluentAssertions;
using NexusMods.Backend.FileExtractor.Extractors;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace NexusMods.Backend.Tests.FileExtractor;

public class SevenZipExtractionTests : AFileExtractorTest
{
    [Fact]
    public async Task Test_Issue3003()
    {
        const string fileName = "zip-with-spaces.zip";
        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(fileName);
        archivePath.FileExists.Should().BeTrue();

        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(archivePath, destination, TestContext.Current.CancellationToken);

        var files = destination.Path.EnumerateFiles().ToArray();
        files.Should().OnlyContain(x => x.FileExists);
    }

    // 7z restores the Unix mode stored in a .7z or .rar entry, and an archive that stores 000 left collection
    // downloads unable to read their own extracted files. 7z-with-unix-modes.7z holds scripts/mod.reds at
    // mode 000 and bin/run.sh at 755.
    [Fact]
    public async Task ExtractedFilesAreReadableWhenTheArchiveStoresNoPermissions()
    {
        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(UnixModesArchive, destination, TestContext.Current.CancellationToken);

        var file = destination.Path.Combine("scripts/mod.reds");
        (await File.ReadAllTextAsync(file.ToString(), TestContext.Current.CancellationToken)).Should().Be("func Main() {}");
    }

    [Fact]
    public async Task ExtractionKeepsAnExecuteBitTheArchiveSet()
    {
        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(UnixModesArchive, destination, TestContext.Current.CancellationToken);

        File.GetUnixFileMode(destination.Path.Combine("bin/run.sh").ToString()).Should().HaveFlag(UnixFileMode.UserExecute);
    }

    private AbsolutePath UnixModesArchive => FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-unix-modes.7z");

    [Theory]
    [InlineData("2024-04-16 06:10:44 D....            0            0  .", false, null, true)]
    [InlineData("2024-04-16 06:10:44 D....            0            0  ..", false, null, true)]
    [InlineData("2024-04-16 06:10:44 D....            0            0  foo ", true, "foo ", true)]
    [InlineData("2024-04-16 06:10:44 .....            0            0  foo ", true, "foo ", false)]
    [InlineData("2024-04-16 06:10:44 .....            0            0  foo", false, null, false)]
    [InlineData("2024-04-16 06:10:44 D....            0            0  foo", false, null, true)]
    public void Test_TryParseListCommandOutput(string input, bool expected, string? expectedFileName, bool expectedIsDirectory)
    {
        var actual = SevenZipExtractor.TryParseListCommandOutput(input, out var fileName, out var isDirectory);
        actual.Should().Be(expected);

        fileName.Should().Be(expectedFileName);
        isDirectory.Should().Be(expectedIsDirectory);
    }

    [Fact]
    public async Task CanExtractToLongPath()
    {
        await using var tempFolder = TemporaryFileManager.CreateFolder();
        var dest = tempFolder.Path;

        // Create a long path
        while (!(dest.GetFullPathLength() > 280))
        {
            dest = dest.Combine("subfolder");
            FileSystem.CreateDirectory(dest);
        }

        dest.GetFullPathLength().Should().BeGreaterThan(280);

        FileSystem.CreateDirectory(dest);

        var file = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources/data_7zip_lzma2.7z");

        var act = async () => await FileExtractor.ExtractAllAsync(file, dest, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        var actual = await tempFolder.Path.EnumerateFiles()
            .ToAsyncEnumerable()
            .SelectAwait(async f => (f.RelativeTo(dest), await f.XxHash3Async()))
            .ToArrayAsync(TestContext.Current.CancellationToken);

        (RelativePath, Hash)[] expected = [
            ("deepFolder/deepFolder2/deepFolder3/deepFolder4/deepFile.txt", (Hash)0x3F0AB4D495E35A9A),
            ("folder1/folder1file.txt", (Hash)0x8520436F06348939),
            ("rootFile.txt", (Hash)0x818A82701BC1CC30),
        ];

        actual.Should().BeEquivalentTo(expected);
    }
}
