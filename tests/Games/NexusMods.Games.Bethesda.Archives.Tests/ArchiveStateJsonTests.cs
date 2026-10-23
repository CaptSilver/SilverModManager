using System.Text.Json;
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives.Tests;

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
}
