using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// Bytes waiting for the archive to be written, kept in a temporary file so a build does not hold every
/// member in memory.
/// </summary>
internal sealed class StagedData : IAsyncDisposable
{
    private readonly TemporaryPath _file;

    private StagedData(TemporaryPath file, long length)
    {
        _file = file;
        Length = length;
    }

    public long Length { get; }

    public static async Task<StagedData> Create(
        TemporaryFileManager manager, Func<Stream, Task> write)
    {
        var file = manager.CreateFile();
        try
        {
            long length;
            await using (var output = file.Path.Create())
            {
                await write(output);
                await output.FlushAsync();
                length = output.Length;
            }

            return new StagedData(file, length);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async Task CopyTo(Stream destination, CancellationToken cancellationToken)
    {
        await using var input = _file.Path.Read();
        await input.CopyToAsync(destination, cancellationToken);
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();
}
