using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.Backend.FileExtractor;
using SilverModManager.Backend.FileExtractor.Extractors;
using SilverModManager.Sdk.IO;
using Extractor = SilverModManager.Backend.FileExtractor.FileExtractor;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class FileExtractorFallbackTests
{
    private sealed class ScriptedExtractor(Priority priority, Action<AbsolutePath>? run = null, Exception? failure = null) : IExtractor
    {
        public int Calls { get; private set; }
        public FileType[] SupportedSignatures { get; } = [FileType.ZIP];
        public Extension[] SupportedExtensions { get; } = [new(".zip")];
        public Priority DeterminePriority(IEnumerable<FileType> signatures) => priority;

        public Task ExtractAllAsync(IStreamFactory source, AbsolutePath destination, CancellationToken token = default)
        {
            Calls++;
            run?.Invoke(destination);
            if (failure is not null) throw failure;
            return Task.CompletedTask;
        }
    }

    private static MemoryStreamFactory ZipSignature() => new("a.zip", new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]));

    private static Extractor Create(params IExtractor[] extractors) => new(NullLogger<Extractor>.Instance, extractors);

    private static TemporaryFileManager CreateTemporaryFileManager()
    {
        var prefix = FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine($"SilverModManager.Backend.Tests-{Guid.NewGuid()}"));
        return new TemporaryFileManager(FileSystem.Shared, prefix);
    }

    [Fact]
    public async Task APathExceptionFromOneExtractorFallsBackToTheNext()
    {
        var first = new ScriptedExtractor(Priority.Highest, failure: new PathException("Input contains invalid characters"));
        var second = new ScriptedExtractor(Priority.Normal, dest => File.WriteAllText(dest.Combine("ok.txt").ToString(), "ok"));
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var destination = temporaryFileManager.CreateFolder();

        await Create(first, second).ExtractAllAsync(ZipSignature(), destination, TestContext.Current.CancellationToken);

        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        destination.Path.Combine("ok.txt").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task FilesAFailedExtractorLeftBehindAreRemovedBeforeTheNextOneRuns()
    {
        var first = new ScriptedExtractor(Priority.Highest,
            dest => File.WriteAllText(dest.Combine("half.txt").ToString(), "partial"),
            new PathException("Input contains invalid characters"));
        var second = new ScriptedExtractor(Priority.Normal, dest => File.WriteAllText(dest.Combine("ok.txt").ToString(), "ok"));
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var destination = temporaryFileManager.CreateFolder();

        await Create(first, second).ExtractAllAsync(ZipSignature(), destination, TestContext.Current.CancellationToken);

        destination.Path.EnumerateFiles().Select(f => f.FileName.ToString()).Should().Equal("ok.txt");
    }

    [Fact]
    public async Task FilesLeftBehindInADestinationThatDidNotExistAreRemovedToo()
    {
        var first = new ScriptedExtractor(Priority.Highest,
            dest =>
            {
                Directory.CreateDirectory(dest.ToString());
                File.WriteAllText(dest.Combine("half.txt").ToString(), "partial");
            },
            new PathException("Input contains invalid characters"));
        var second = new ScriptedExtractor(Priority.Normal, dest =>
        {
            Directory.CreateDirectory(dest.ToString());
            File.WriteAllText(dest.Combine("ok.txt").ToString(), "ok");
        });
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var parent = temporaryFileManager.CreateFolder();
        var destination = parent.Path.Combine("not-yet");

        await Create(first, second).ExtractAllAsync(ZipSignature(), destination, TestContext.Current.CancellationToken);

        destination.EnumerateFiles().Select(f => f.FileName.ToString()).Should().Equal("ok.txt");
    }

    [Fact]
    public async Task WhenEveryExtractorFailsTheFailureCarriesTheLastCause()
    {
        var first = new ScriptedExtractor(Priority.Highest, failure: new PathException("bad name"));
        var second = new ScriptedExtractor(Priority.Normal, failure: new InvalidDataException("corrupt"));
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var destination = temporaryFileManager.CreateFolder();

        var act = async () => await Create(first, second).ExtractAllAsync(ZipSignature(), destination, TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<FileExtractionException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidDataException>();
    }
}
