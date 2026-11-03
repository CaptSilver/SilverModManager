namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// A seekable scratch file that deletes itself on dispose. Delta application needs random access
/// to its basis, and bases can be 20 MB to several GB, so they go to disk rather than memory.
/// </summary>
internal sealed class TempFile : IDisposable
{
    private readonly string _path;

    public FileStream Stream { get; }

    private TempFile(string directory)
    {
        _path = Path.Combine(directory, $"silvermod-patch-{Guid.NewGuid():N}.tmp");
        Stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a new file in <paramref name="directory"/> and leaves the file positioned at the start.
    /// The directory is the caller's to choose: bases run to gigabytes, and the system temp folder is often a RAM disk.
    /// </summary>
    public static async Task<TempFile> CopyOf(Stream source, string directory, CancellationToken token)
    {
        var file = new TempFile(directory);
        try
        {
            await source.CopyToAsync(file.Stream, token);
            file.Stream.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies each source in order into one new file in <paramref name="directory"/>, positioned at the start.
    /// </summary>
    public static async Task<TempFile> Concatenation(IEnumerable<Stream> sources, string directory, CancellationToken token)
    {
        var file = new TempFile(directory);
        try
        {
            foreach (var source in sources)
                await source.CopyToAsync(file.Stream, token);
            file.Stream.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose() => Stream.Dispose();
}
