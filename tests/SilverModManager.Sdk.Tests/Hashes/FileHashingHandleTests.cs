using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

/// <summary>
/// A file reached through a directory descriptor has no path that can be trusted, so it is hashed from the handle it
/// was opened as. The digest has to be the one hashing the same file by path gives.
/// </summary>
public class FileHashingHandleTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("silvermod-handle-hash-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task AHandleHashesLikeItsPath()
    {
        var path = Path.Combine(_directory, "data.bin");
        var bytes = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var byPath = await FileHashing.XxHash3(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(path), TestContext.Current.CancellationToken);

        using SafeFileHandle handle = File.OpenHandle(path);
        var byHandle = await FileHashing.XxHash3(handle, TestContext.Current.CancellationToken);

        byHandle.Should().Be(byPath);
    }

    [Fact]
    public async Task HashingAnEmptyFileThroughItsHandleWorks()
    {
        var path = Path.Combine(_directory, "empty.bin");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);

        using SafeFileHandle handle = File.OpenHandle(path);

        var hash = await FileHashing.XxHash3(handle, TestContext.Current.CancellationToken);

        hash.Should().Be(await FileHashing.XxHash3(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(path), TestContext.Current.CancellationToken));
    }
}
