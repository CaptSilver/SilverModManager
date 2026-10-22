using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;

namespace NexusMods.Backend.Tests.FileExtractor;

/// <summary>
/// Records what extraction does with a zip entry that climbs out of the destination with "..". Nothing
/// downstream relies on it, but a change in this behaviour should be noticed: a leading ".." makes the
/// extraction fail with a <see cref="PathException"/>, and a ".." after a folder name is dropped from the
/// path. Either way nothing is written outside the destination.
/// </summary>
public class ArchiveTraversalTests : AFileExtractorTest
{
    private async Task<(Exception? Failure, string[] Inside, string[] Outside)> Extract(string entryName)
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var destination = container.Path.Combine("a/b/extracted");
        destination.CreateDirectory();

        var archive = container.Path.Combine("traversal.zip");
        await using (var stream = archive.Create())
        await using (var output = new ZipOutputStream(stream))
        {
            await output.PutNextEntryAsync(new ZipEntry(entryName), TestContext.Current.CancellationToken);
            await output.WriteAsync(Encoding.UTF8.GetBytes("payload"), TestContext.Current.CancellationToken);
            await output.CloseEntryAsync(TestContext.Current.CancellationToken);
        }

        Exception? failure = null;
        try
        {
            await FileExtractor.ExtractAllAsync(archive, destination, TestContext.Current.CancellationToken);
        }
        catch (Exception e)
        {
            failure = e;
        }

        var inside = destination.EnumerateFiles().Select(f => f.RelativeTo(destination).ToString()).Order().ToArray();
        var outside = container.Path.EnumerateFiles()
            .Where(f => !f.InFolder(destination) && f != archive)
            .Select(f => f.RelativeTo(container.Path).ToString())
            .ToArray();
        return (failure, inside, outside);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    public async Task AnEntryThatStartsWithDotDotMakesTheExtractionFail(string entryName)
    {
        var (failure, inside, outside) = await Extract(entryName);

        failure.Should().BeOfType<PathException>();
        inside.Should().BeEmpty();
        outside.Should().BeEmpty();
    }

    [Fact]
    public async Task ADotDotAfterAFolderNameIsDroppedFromTheExtractedPath()
    {
        var (failure, inside, outside) = await Extract("sub/../../escaped.txt");

        failure.Should().BeNull();
        inside.Should().Equal("sub/escaped.txt");
        outside.Should().BeEmpty();
    }
}
