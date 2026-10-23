namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// A seekable scratch file that deletes itself on dispose. Delta application needs random access
/// to its basis, and bases can be 20 MB to several GB, so they go to disk rather than memory.
/// </summary>
internal sealed class TempFile : IDisposable
{
    private readonly string _path;

    public FileStream Stream { get; }

    private TempFile()
    {
        _path = Path.Combine(Path.GetTempPath(), $"silvermod-patch-{Guid.NewGuid():N}.tmp");
        Stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a new temp file and leaves the file positioned at the start.
    /// </summary>
    public static async Task<TempFile> CopyOf(Stream source, CancellationToken token)
    {
        var file = new TempFile();
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
    /// Copies each source in order into one new temp file, positioned at the start.
    /// </summary>
    public static async Task<TempFile> Concatenation(IEnumerable<Stream> sources, CancellationToken token)
    {
        var file = new TempFile();
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
