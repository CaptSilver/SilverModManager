using R3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Library.Pickup;

/// <summary>
/// A file somebody is waiting for the user to download in a browser: the name the browser will give it, its size, and
/// who to hand it to once it turns up in the Downloads folder.
/// </summary>
/// <param name="XxHash64">The hash of the file's bytes, or null for a source that only knows the name and size; the sink then checks the file against the source's own digest.</param>
public sealed record ExpectedDownload(string FileName, long Size, FileIdentity? XxHash64, IPickupSink Sink);

/// <summary>
/// Whoever is waiting for a download takes the file that matched its expectation.
/// </summary>
public interface IPickupSink
{
    /// <summary>
    /// Takes the file from the user's Downloads folder, which is never moved or deleted: <paramref name="link"/> has
    /// <c>LibcLinks.TryLink</c>'s shape and is how a sink hard-links the file before it falls back to a copy.
    /// </summary>
    Task Accept(NexusMods.Paths.AbsolutePath file, Func<string, string, int> link, CancellationToken cancellationToken);
}

/// <summary>
/// The downloads the app is waiting for, from every source at once. <see cref="DownloadsPickup"/> watches the Downloads
/// folder for exactly these names.
/// </summary>
public interface IExpectedDownloads
{
    IReadOnlyList<ExpectedDownload> Current { get; }

    /// <summary>Raised after an expectation was added or ended.</summary>
    Observable<Unit> Changed { get; }

    /// <summary>Adds an expectation; disposing the result ends it.</summary>
    IDisposable Expect(ExpectedDownload download);
}
