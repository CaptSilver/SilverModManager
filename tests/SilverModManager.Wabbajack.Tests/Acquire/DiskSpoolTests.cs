using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class DiskSpoolTests : IDisposable
{
    private readonly string _root = TestScratch.Combine($"DiskSpool-{Guid.NewGuid()}").ToString();
    private readonly TemporaryFileManager _temp;

    public DiskSpoolTests() => _temp = new TemporaryFileManager(FileSystem.Shared, FileSystem.Shared.FromUnsanitizedFullPath(_root));

    public void Dispose() => _temp.Dispose();

    private static readonly byte[] Content = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    private static Task WriteContent(Stream spool, CancellationToken cancellationToken) => new MemoryStream(Content).CopyToAsync(spool, cancellationToken);

    private string[] FilesUnderRoot() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray() : [];

    [Fact]
    public async Task Fill_PutsTheFileUnderTheManagersRoot()
    {
        await using var spool = await DiskSpool.Fill(_temp, 4096, WriteContent, CancellationToken.None);

        spool.Should().BeOfType<FileStream>().Which.Name.Should().StartWith(Path.GetFullPath(_root));
        FilesUnderRoot().Should().ContainSingle();
    }

    [Fact]
    public async Task Fill_MakesTheTempFolderAgainWhenItWasRemoved()
    {
        using (_temp.CreateFile()) { }
        Directory.Delete(_root, recursive: true);

        await using var spool = await DiskSpool.Fill(_temp, 4096, WriteContent, CancellationToken.None);

        FilesUnderRoot().Should().ContainSingle();
    }

    [Fact]
    public async Task Fill_RewindsToTheStart()
    {
        await using var spool = await DiskSpool.Fill(_temp, 4096, WriteContent, CancellationToken.None);

        spool.Position.Should().Be(0);
        using var read = new MemoryStream();
        await spool.CopyToAsync(read);
        read.ToArray().Should().Equal(Content);
    }

    [Fact]
    public async Task Fill_WhenTheWriteFails_LeavesNoFileBehind()
    {
        var failure = await Record.ExceptionAsync(() => DiskSpool.Fill(_temp, 4096,
            (_, _) => throw new InvalidDataException("the source broke"), CancellationToken.None));

        failure.Should().BeOfType<InvalidDataException>();
        FilesUnderRoot().Should().BeEmpty();
    }

    [Fact]
    public async Task DisposingTheSpool_DeletesItsFile()
    {
        var spool = await DiskSpool.Fill(_temp, 4096, WriteContent, CancellationToken.None);
        FilesUnderRoot().Should().ContainSingle();

        await spool.DisposeAsync();

        FilesUnderRoot().Should().BeEmpty();
    }
}
