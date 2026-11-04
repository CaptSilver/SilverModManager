using FluentAssertions;

namespace SilverModManager.CLI.Tests.VerbTests;

public class ExtractArchive(IServiceProvider provider) : AVerbTest(provider)
{
    [Fact]
    public async Task CanExtractArchive()
    {
        await using var folder = TemporaryFileManager.CreateFolder();

        await Run("extract-archive", "-i", Data7ZipLZMA2.ToString(), "-o", folder.Path.ToString());

        folder.Path.EnumerateFiles().Count().Should().Be(3);
    }

    // data_7zip_lzma2.7z stores folder1/folder1file.txt and rootFile.txt.
    [Fact]
    public async Task ALinkAlreadyInTheOutputFolderIsNeverWrittenThrough()
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var outside = container.Path.Combine("outside");
        outside.CreateDirectory();
        var secret = outside.Combine("secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);
        var output = container.Path.Combine("out");
        output.CreateDirectory();
        Directory.CreateSymbolicLink(output.Combine("folder1").ToString(), outside.ToString());
        File.CreateSymbolicLink(output.Combine("rootFile.txt").ToString(), secret.ToString());

        await Run("extract-archive", "-i", Data7ZipLZMA2.ToString(), "-o", output.ToString());

        outside.EnumerateFiles().Select(f => f.FileName).Should().BeEquivalentTo(["secret.txt"]);
        File.ReadAllText(secret.ToString()).Should().Be("private");
        new FileInfo(output.Combine("folder1").ToString()).LinkTarget.Should().Be(outside.ToString());
        new FileInfo(output.Combine("rootFile.txt").ToString()).LinkTarget.Should().BeNull();
        File.Exists(output.Combine("deepFolder/deepFolder2/deepFolder3/deepFolder4/deepFile.txt").ToString()).Should().BeTrue();
    }

    // data_7zip_lzma2.7z stores deepFolder/deepFolder2/deepFolder3/deepFolder4/deepFile.txt.
    [Fact]
    public async Task ANestedLinkAlreadyInTheOutputFolderIsNeverWrittenThrough()
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var outside = container.Path.Combine("outside");
        outside.CreateDirectory();
        var output = container.Path.Combine("out");
        output.Combine("deepFolder/deepFolder2").CreateDirectory();
        Directory.CreateSymbolicLink(output.Combine("deepFolder/deepFolder2/deepFolder3").ToString(), outside.ToString());

        await Run("extract-archive", "-i", Data7ZipLZMA2.ToString(), "-o", output.ToString());

        Directory.EnumerateFileSystemEntries(outside.ToString()).Should().BeEmpty();
        new FileInfo(output.Combine("deepFolder/deepFolder2/deepFolder3").ToString()).LinkTarget.Should().Be(outside.ToString());
        File.Exists(output.Combine("rootFile.txt").ToString()).Should().BeTrue();
    }
}
