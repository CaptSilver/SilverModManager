using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NSubstitute;

namespace NexusMods.Deployment.Staging.Tests;

/// <summary>
/// The bytes the substitute <see cref="IFileStore"/> writes when asked to extract a hash.
/// </summary>
public class FakeFileStoreContents
{
    public Dictionary<Hash, byte[]> Files { get; } = new();

    /// <summary>
    /// Paths of every extraction request, in order, so tests can see where the store extracted to.
    /// </summary>
    public List<AbsolutePath> ExtractedTo { get; } = new();

    public static IFileStore CreateFileStore(FakeFileStoreContents contents)
    {
        var store = Substitute.For<IFileStore>();
        store.ExtractFiles(Arg.Any<IEnumerable<(Hash, AbsolutePath)>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<(int, int)>?>())
            .Returns(call =>
            {
                foreach (var (hash, dest) in call.Arg<IEnumerable<(Hash, AbsolutePath)>>())
                {
                    contents.ExtractedTo.Add(dest);
                    dest.Parent.CreateDirectory();
                    File.WriteAllBytes(dest.ToString(), contents.Files[hash]);
                }
                return Task.CompletedTask;
            });
        return store;
    }
}
