using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class GeneratedFileWriterTests(ITestOutputHelper helper) : InstallTestBase<GeneratedFileWriterTests>(helper)
{
    private GeneratedFileWriter Writer => new(FileStore, TemporaryFileManager);

    [Fact]
    public async Task AGeneratedFileIsHashedByBothDigestsInOnePass()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 11)).ToArray();

        await using var file = await Writer.Generate(async output => await output.WriteAsync(bytes), CancellationToken.None);

        file.XxHash3.Should().Be(bytes.xxHash3());
        file.XxHash64.Should().Be(Hash64(bytes));
        file.Size.Value.Should().Be((ulong)bytes.Length);
    }

    [Fact]
    public async Task StoringKeepsTheBytesInTheFileStoreAndRemovesTheTemporaryFiles()
    {
        // More than two batches, so the batching is exercised.
        var generated = new List<GeneratedFile>();
        for (var i = 0; i < GeneratedFileWriter.StoreBatchSize * 2 + 5; i++)
        {
            var bytes = BitConverter.GetBytes(i).Concat(Enumerable.Repeat((byte)i, 20)).ToArray();
            generated.Add(await Writer.Generate(async output => await output.WriteAsync(bytes), CancellationToken.None));
        }
        var paths = generated.Select(g => g.Path).ToArray();

        await Writer.Store(generated, CancellationToken.None);

        foreach (var file in generated)
            (await FileStore.HaveFile(file.XxHash3)).Should().BeTrue();
        paths.Should().OnlyContain(p => !p.FileExists);
    }

    [Fact]
    public async Task AFileTheStoreAlreadyHoldsIsNotStoredAgain()
    {
        var bytes = "same bytes"u8.ToArray();
        await using var first = await Writer.Generate(async o => await o.WriteAsync(bytes), CancellationToken.None);
        await Writer.Store([first], CancellationToken.None);
        await using var second = await Writer.Generate(async o => await o.WriteAsync(bytes), CancellationToken.None);

        await Writer.Store([second], CancellationToken.None);

        (await FileStore.HaveFile(bytes.xxHash3())).Should().BeTrue();
    }

    [Fact]
    public async Task ATemporaryFileHoldsWhatWasWrittenAndGoesWhenDisposed()
    {
        var temporary = await Writer.WriteTemporary(async output => await output.WriteAsync("input"u8.ToArray()));
        var path = temporary.Path;

        (await path.ReadAllTextAsync()).Should().Be("input");

        await temporary.DisposeAsync();
        path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedStoreStillRemovesTheTemporaryFilesOfBatchesItNeverReached()
    {
        var generated = new List<GeneratedFile>();
        for (var i = 0; i < GeneratedFileWriter.StoreBatchSize + 3; i++)
        {
            var bytes = BitConverter.GetBytes(i).Concat(Enumerable.Repeat((byte)i, 20)).ToArray();
            generated.Add(await Writer.Generate(async output => await output.WriteAsync(bytes), CancellationToken.None));
        }
        var paths = generated.Select(g => g.Path).ToArray();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var store = () => Writer.Store(generated, cancelled.Token);

        await store.Should().ThrowAsync<OperationCanceledException>();
        paths.Should().OnlyContain(p => !p.FileExists);
    }
}
