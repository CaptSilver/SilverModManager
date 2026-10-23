// Follows Wabbajack.Compression.BSA/BSADispatch.cs (CreateBuilder) (GPL-3.0).
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

public static class BethesdaArchiveBuilder
{
    /// <summary>
    /// Starts a builder for the archive kind <paramref name="state"/> belongs to. Members are staged in files
    /// from <paramref name="temporaryFileManager"/>.
    /// </summary>
    public static IBethesdaArchiveBuilder Create(ArchiveState state, TemporaryFileManager temporaryFileManager) => state switch
    {
        Tes3ArchiveState tes3 => new Tes3ArchiveBuilder(tes3, temporaryFileManager),
        Tes5ArchiveState tes5 => new Tes5ArchiveBuilder(tes5, temporaryFileManager),
        Ba2ArchiveState ba2 => new Ba2ArchiveBuilder(ba2, temporaryFileManager),
        _ => throw new BethesdaArchiveException($"Cannot build an archive from a {state.GetType().Name}"),
    };
}
