using System.Text.Json;
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

// The literals follow the shape Wabbajack writes into a modlist's CreateBSA directive.
public class ArchiveStateJsonTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("BSAState, Compression.BSA")]
    [InlineData("BSAState")]
    public void BsaStateShouldParseBothTypeNames(string type)
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            $$"""{"$type": "{{type}}", "Magic": "BSA\u0000", "Version": 105, "ArchiveFlags": 7, "FileFlags": 3}"""));

        state.Should().Be(new Tes5ArchiveState("BSA\0", 105, 7, 3));
    }

    [Theory]
    [InlineData("TES3State")]
    [InlineData("TES3State, Compression.BSA")]
    public void Tes3StateShouldParseBothTypeNames(string type)
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            $$"""{"$type": "{{type}}", "FileCount": 12, "DataOffset": 4096, "HashOffset": 900, "VersionNumber": 256}"""));

        state.Should().Be(new Tes3ArchiveState(12, 4096, 900, 256));
    }

    [Theory]
    [InlineData("BSAFileState, Compression.BSA")]
    [InlineData("BSAFile")]
    public void BsaFileShouldParseBothTypeNames(string type)
    {
        var state = ArchiveStateJson.ParseFileState(Json(
            $$"""{"$type": "{{type}}", "FlipCompression": true, "Index": 3, "Path": "meshes\\armor\\cuirass.nif"}"""));

        state.Should().Be(new Tes5FileState(3, RelativePath.FromUnsanitizedInput("meshes/armor/cuirass.nif"), true));
    }

    [Fact]
    public void Tes3FileShouldParse()
    {
        var state = ArchiveStateJson.ParseFileState(Json(
            """{"$type": "TES3File", "Offset": 10, "NameOffset": 20, "Hash1": 30, "Hash2": 40, "Size": 50, "Index": 2, "Path": "a\\b.nif"}"""));

        state.Should().Be(new Tes3FileState(2, RelativePath.FromUnsanitizedInput("a/b.nif"), 10, 20, 30, 40, 50));
    }

    [Fact]
    public void TypeMatchingShouldIgnoreFieldCase()
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            """{"$type": "BSAState", "magic": "BSA\u0000", "version": 104, "archiveFlags": 3, "fileFlags": 1}"""));

        state.Should().Be(new Tes5ArchiveState("BSA\0", 104, 3, 1));
    }

    [Fact]
    public void UnknownTypeShouldThrow()
    {
        var act = () => ArchiveStateJson.ParseArchiveState(Json("""{"$type": "Nope, Compression.BSA"}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*Nope*");
    }

    [Fact]
    public void MissingTypeShouldThrow()
    {
        var act = () => ArchiveStateJson.ParseFileState(Json("""{"Index": 1}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*$type*");
    }

    [Fact]
    public void MissingFieldShouldThrowNamingIt()
    {
        var act = () => ArchiveStateJson.ParseArchiveState(Json("""{"$type": "BSAState", "Magic": "BSA\u0000"}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*Version*");
    }

    [Fact]
    public void FieldOfTheWrongTypeShouldThrowBethesdaArchiveException()
    {
        var act = () => ArchiveStateJson.ParseFileState(Json("""{"$type": "BSAFile", "FlipCompression": "yes", "Index": 0, "Path": "a"}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*FlipCompression*");
    }

    [Theory]
    [InlineData("BA2State, Compression.BSA")]
    [InlineData("BA2State")]
    public void Ba2StateShouldParseBothTypeNames(string type)
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            $$"""{"$type": "{{type}}", "HasNameTable": true, "HeaderMagic": "BTDX", "Type": 1, "Version": 3, "Unknown1": 1, "Unknown2": 2, "Compression": 3}"""));

        state.Should().Be(new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 3, 1, 2, 3));
    }

    [Fact]
    public void Ba2StateWrittenBeforeStarfieldShouldStillParse()
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            """{"$type": "BA2State, Compression.BSA", "HasNameTable": true, "HeaderMagic": "BTDX", "Type": 0, "Version": 1}"""));

        state.Should().Be(new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0));
    }

    [Fact]
    public void Ba2StateShouldAcceptTheTypeAsAName()
    {
        var state = ArchiveStateJson.ParseArchiveState(Json(
            """{"$type": "BA2State", "HasNameTable": false, "HeaderMagic": "BTDX", "Type": "DX10", "Version": 1}"""));

        state.Should().BeOfType<Ba2ArchiveState>().Which.Type.Should().Be(Ba2EntryType.Dx10);
    }

    [Fact]
    public void Ba2StateWithAnUnknownTypeShouldThrow()
    {
        var act = () => ArchiveStateJson.ParseArchiveState(Json(
            """{"$type": "BA2State", "HasNameTable": false, "HeaderMagic": "BTDX", "Type": 9, "Version": 1}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*Type*");
    }

    [Theory]
    [InlineData("BA2File")]
    [InlineData("BA2FileEntryState, Compression.BSA")]
    public void Ba2FileShouldParseBothTypeNames(string type)
    {
        var state = ArchiveStateJson.ParseFileState(Json(
            $$"""{"$type": "{{type}}", "Extension": "nif", "Compressed": true, "Align": 3735941133, "Flags": 1048832, "DirHash": 5, "NameHash": 6, "Index": 4, "Path": "meshes\\a.nif"}"""));

        state.Should().Be(new Ba2FileState(4, RelativePath.FromUnsanitizedInput("meshes/a.nif"), "nif", true, 3735941133, 1048832, 5, 6));
    }

    [Theory]
    [InlineData("BA2DX10Entry")]
    [InlineData("BA2DX10Entry, Compression.BSA")]
    public void Ba2Dx10EntryShouldParseEveryField(string type)
    {
        var state = ArchiveStateJson.ParseFileState(Json(
            $$"""
            {"$type": "{{type}}", "Index": 7, "Path": "textures\\t.dds",
             "Chunks": [{"Compressed": true, "Align": 11, "EndMip": 3, "StartMip": 1, "FullSz": 4096},
                        {"Compressed": false, "Align": 12, "EndMip": 5, "StartMip": 4, "FullSz": 64}],
             "PixelFormat": 98, "NumMips": 6, "Width": 1024, "Height": 512, "ChunkHdrLen": 24, "Unk8": 2,
             "DirHash": 100, "Extension": "dds", "NameHash": 200, "IsCubeMap": 1, "TileMode": 8}
            """));

        state.Should().Be(new Ba2Dx10FileState(
            Index: 7, Path: RelativePath.FromUnsanitizedInput("textures/t.dds"),
            Chunks: [new Ba2Chunk(true, 11, 3, 1, 4096), new Ba2Chunk(false, 12, 5, 4, 64)],
            PixelFormat: 98, NumMips: 6, Width: 1024, Height: 512, ChunkHdrLen: 24, Unk8: 2,
            DirHash: 100, Extension: "dds", NameHash: 200, IsCubeMap: 1, TileMode: 8));
    }

    [Fact]
    public void Ba2Dx10EntryWithoutChunksShouldThrowNamingTheField()
    {
        var act = () => ArchiveStateJson.ParseFileState(Json(
            """{"$type": "BA2DX10Entry", "Index": 0, "Path": "a.dds"}"""));
        act.Should().Throw<BethesdaArchiveException>().WithMessage("*Chunks*");
    }
}
