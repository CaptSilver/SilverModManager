using NexusMods.Games.Bethesda.Archives;
using NexusMods.Paths;

namespace NexusMods.Wabbajack.Acquire;

/// <summary>
/// Reads one member out of a Bethesda archive (<c>.bsa</c> or <c>.ba2</c>) for a list path that goes through it: the
/// library does not expand these, but lists copy single textures and meshes out of them, including out of the game's own.
/// </summary>
public sealed class BsaMemberOpener : INestedArchiveOpener
{
    private const int BufferSize = 1024 * 1024;

    public bool CanOpen(RelativePath name)
        => name.Extension == new Extension(".bsa") || name.Extension == new Extension(".ba2");

    public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
    {
        // The readers seek. A store stream that cannot is spooled; the reader then owns (and disposes) the spool.
        var source = archive.CanSeek ? archive : await Spool(archive, cancellationToken);

        IBethesdaArchiveReader reader;
        try
        {
            reader = await BethesdaArchive.Open(source, cancellationToken);
        }
        catch (BethesdaArchiveException e)
        {
            await source.DisposeAsync();
            throw new MemberNotFoundException($"cannot look for '{member}': {e.Message}");
        }

        await using (reader)
        {
            var wanted = Fold(member.ToString());
            var entry = reader.Entries.FirstOrDefault(e => Fold(e.Path.ToString()) == wanted);
            if (entry is null) return null;

            // Returned to the resolver, which hashes it and stores it, so it has to outlive the archive.
            var copy = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await entry.CopyToAsync(copy, cancellationToken);
                copy.Position = 0;
                return copy;
            }
            catch (BethesdaArchiveException e)
            {
                await copy.DisposeAsync();
                throw new MemberNotFoundException($"cannot read '{member}': {e.Message}");
            }
            catch
            {
                await copy.DisposeAsync();
                throw;
            }
        }
    }

    private static async Task<Stream> Spool(Stream archive, CancellationToken cancellationToken)
    {
        var spool = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await archive.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    private static string Fold(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
