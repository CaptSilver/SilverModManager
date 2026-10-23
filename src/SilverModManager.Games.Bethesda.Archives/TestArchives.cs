using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// Small archives and textures for tests of code that reads archive members, so those tests do not each carry
/// their own writer.
/// </summary>
internal static class TestArchives
{
    public static readonly RelativePath FirstMemberPath = RelativePath.FromUnsanitizedInput("meshes/first.nif");

    public static readonly RelativePath SecondMemberPath = RelativePath.FromUnsanitizedInput("textures/second.dds");

    public static byte[] FirstMemberData => "first member of the archive"u8.ToArray();

    public static byte[] SecondMemberData => "second member of the archive"u8.ToArray();

    /// <summary>
    /// A Skyrim LE (version 104) archive of <see cref="FirstMemberData"/> and <see cref="SecondMemberData"/>,
    /// stored uncompressed, with folder and file names.
    /// </summary>
    public static async Task<byte[]> Tes5Archive(TemporaryFileManager temporaryFileManager)
    {
        await using var builder = BethesdaArchiveBuilder.Create(
            new Tes5ArchiveState("BSA\0", 104, Flags, 0), temporaryFileManager);
        await builder.AddFile(new Tes5FileState(0, FirstMemberPath, false), new MemoryStream(FirstMemberData), CancellationToken.None);
        await builder.AddFile(new Tes5FileState(0, SecondMemberPath, false), new MemoryStream(SecondMemberData), CancellationToken.None);

        var output = new MemoryStream();
        await builder.Build(output, CancellationToken.None);
        return output.ToArray();
    }

    // Folder names and file names.
    private const uint Flags = 0x3;

    /// <summary>
    /// A 4x4 BC1 texture with a single mip, as a DDS file: the header, then one 8 byte block.
    /// </summary>
    public static byte[] Bc1Dds()
    {
        const byte bc1 = 71;
        // Colour 0 is 0xF800 (red), colour 1 black, and all sixteen texels pick colour 0.
        byte[] block = [0x00, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        return [.. DdsHeader.Build(4, 4, 1, bc1, isCubeMap: false), .. block];
    }
}
