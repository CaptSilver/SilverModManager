using System.Text;
using FluentAssertions;
using SilverModManager.Games.Larian.Engine.Utils.LsxXmlParsing;
using SilverModManager.Games.Larian.Engine.Utils.PakParsing;
using NexusMods.Paths;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class BG3PakParsingTests
{
    private readonly IFileSystem _fs;

    public BG3PakParsingTests(IFileSystem fs)
    {
        _fs = fs;
    }

    [Theory]
    [InlineData("AllItems.pak")]
    [InlineData("dnd5rbardcollegeofswordshomebr-hu6v.pak")]
    [InlineData("MoreSpellSlotsAndFeats.pak")]
    [InlineData("Waypoint Inside Emerald Grove.pak")]
    [InlineData("Carry Weight Increased 9000 - X900.pak")]
    [InlineData("MetamagicExtendedQuickenedSP2.pak")]
    public async Task ParsePakMeta_ShouldParseCorrectly(string pakFilePath)
    {
        var fullPath = _fs.GetKnownPath(KnownPath.EntryDirectory).Combine("BaldursGate3/Resources/PakFiles/" + pakFilePath);
        await using var pakFileStream = File.OpenRead(fullPath.ToString());
        var metaFileData = PakFileParser.ParsePakMeta(pakFileStream).MetaFileData;
        var sb = new StringBuilder();
        
        sb.AppendLine("ModuleShortDesc:");
        sb.Append(LsxXmlFormat.SerializeModuleShortDesc(metaFileData.ModuleShortDesc));
        sb.AppendLineN();
        var semanticVersion = metaFileData.ModuleShortDesc.SemanticVersion;
        sb.AppendLine($"Version: {semanticVersion.Major}.{semanticVersion.Minor}.{semanticVersion.Patch}.{semanticVersion.Build}");
        
        foreach (var dependency in metaFileData.Dependencies)
        {
            sb.AppendLine("Dependency:");
            sb.Append(LsxXmlFormat.SerializeModuleShortDesc(dependency));
            sb.AppendLineN();
            var ver = dependency.SemanticVersion;
            sb.AppendLine($"SemanticVersion: {ver.Major}.{ver.Minor}.{ver.Patch}.{ver.Build}");
        }
        await Verify(sb.ToString()).UseParameters(pakFilePath);
    }
    
    [Fact]
    public async Task ParsePakMeta_ShouldThrowOnBadPak()
    {
        var fullPath = _fs.GetKnownPath(KnownPath.EntryDirectory).Combine("BaldursGate3/Resources/PakFiles/malformed.pak");
        await using var pakFileStream = File.OpenRead(fullPath.ToString());
        {
            var act = () => PakFileParser.ParsePakMeta(pakFileStream);
            act.Should().Throw<InvalidDataException>();
        }
    }

    private const string Uuid = "5b3c4b7e-8d6a-4f0e-9f5d-2f1d6b0c9a11";

    private static string Meta(string? type = null)
        => SyntheticPak.MetaLsx("X", "Some Mod", Uuid, SyntheticPak.Version(1), "0", type);

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    public void ReadsTheMetaLsxOfALegacyPak(int version)
    {
        var pak = SyntheticPak.Build(version, ("Mods/X/meta.lsx", Meta()));

        var meta = PakFileParser.ParsePakMeta(new MemoryStream(pak)).MetaFileData;

        meta.ModuleShortDesc.Uuid.Should().Be(Uuid);
        meta.ModuleShortDesc.Name.Should().Be("Some Mod");
    }

    [Fact]
    public void AV13PakWhoseBodyStartsWithTheSignatureIsReadFromTheTrailer()
    {
        // The first entry's stored bytes begin with "LSPK" and a plausible version, as a nested pak would.
        var pak = SyntheticPak.Build(13,
            ("Mods/X/nested.bin", "LSPK\u0012\0\0\0"),
            ("Mods/X/meta.lsx", Meta()));

        PakFileParser.ParsePakMeta(new MemoryStream(pak)).MetaFileData.ModuleShortDesc.Uuid.Should().Be(Uuid);
    }

    [Fact]
    public void DataOffsetStillLocatesPartZeroEntriesNextToAPartOneEntry()
    {
        var pak = SyntheticPak.Build(10, ("Mods/X/meta.lsx", Meta()), ("Mods/X/other.txt", "x"));
        var inPartOne = SyntheticPak.WithArchivePart(pak, "Mods/X/other.txt", 1);

        // The part-0 meta.lsx still resolves, which it only does when DataOffset was added to its offset.
        PakFileParser.ParsePakMeta(new MemoryStream(inPartOne)).MetaFileData.ModuleShortDesc.Uuid.Should().Be(Uuid);
    }

    [Fact]
    public void AnEntryInAnotherPartIsRefusedByName()
    {
        var pak = SyntheticPak.WithArchivePart(SyntheticPak.Build(10, ("Mods/X/meta.lsx", Meta())), "Mods/X/meta.lsx", 1);

        var act = () => PakFileParser.ParsePakMeta(new MemoryStream(pak));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("Mods/X/meta.lsx is stored in part 1 of a multi-part package, which is not supported");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void AMultiPartEntryIsRefusedInEveryFileEntryLayout(int version)
    {
        var pak = SyntheticPak.WithArchivePart(SyntheticPak.Build(version, ("Mods/X/meta.lsx", Meta())), "Mods/X/meta.lsx", 2);

        var act = () => PakFileParser.ParsePakMeta(new MemoryStream(pak));

        act.Should().Throw<InvalidDataException>().WithMessage("*part 2 of a multi-part package*");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(18)]
    public void ASolidPakIsRefusedAtTheHeader(int version)
    {
        var pak = SyntheticPak.WithSolidFlag(SyntheticPak.Build(version, ("Mods/X/meta.lsx", Meta())));

        var act = () => PakFileParser.ParsePakMeta(new MemoryStream(pak));

        act.Should().Throw<InvalidDataException>().WithMessage("solid packages are not supported");
    }

    // A foreign file whose first Int32 happens to be 7, or a damaged legacy header, must fail as a bad pak
    // (the error every caller catches), not as an overflow or a table allocation sized from garbage.
    [Theory]
    [InlineData(7, 17, 0x7FFFFFFFu)]   // FileEntry7 count, end of the bare header
    [InlineData(10, 20, 0x7FFFFFFFu)]  // FileEntry10 count, end of the v10 header
    [InlineData(10, 20, 1_000_000u)]   // a count whose table fits in an int but not in the file
    [InlineData(13, -32, 0u)]          // v13 FileListSize, smaller than its own entry count
    [InlineData(13, -32, 0x7FFFFFF0u)] // v13 FileListSize, past the end of the file
    public void ALegacyFileTableLargerThanThePakIsRefused(int version, int fieldOffset, uint value)
    {
        var pak = SyntheticPak.Build(version, ("Mods/X/meta.lsx", Meta()));
        BitConverter.GetBytes(value).CopyTo(pak, fieldOffset >= 0 ? fieldOffset : pak.Length + fieldOffset);

        var act = () => PakFileParser.ParsePakMeta(new MemoryStream(pak));

        act.Should().Throw<InvalidDataException>().WithMessage("*file table*");
    }

    [Fact]
    public void TheModuleTypeIsRead()
    {
        var pak = SyntheticPak.Build(("Mods/X/meta.lsx", Meta("Adventure")));

        PakFileParser.ParsePakMeta(new MemoryStream(pak)).MetaFileData.ModuleShortDesc.Type.Should().Be("Adventure");
    }

    [Fact]
    public void EveryKnownExtenderConfigIsReturnedByName()
    {
        var pak = SyntheticPak.Build(
            ("Mods/X/meta.lsx", Meta()),
            ("Mods/X/ScriptExtender/Config.json", "{\"RequiredVersion\": 9}"),
            ("Mods/X/OsiToolsConfig.json", "{\"RequiredVersion\": 55}"));

        var meta = PakFileParser.ParsePakMeta(new MemoryStream(pak));

        meta.ScriptExtenderConfigs.Keys.Should().BeEquivalentTo("ScriptExtender/Config.json", "OsiToolsConfig.json");
        meta.ScriptExtenderConfigs["ScriptExtender/Config.json"].SeRequiredVersion.Should().Be(9);
        meta.ScriptExtenderConfigs["OsiToolsConfig.json"].SeRequiredVersion.Should().Be(55);
        meta.ScriptExtenderConfigMetadata.Value.SeRequiredVersion.Should().Be(9);
    }

    [Fact]
    public void APakWithoutExtenderConfigHasNoneInTheMap()
    {
        var meta = PakFileParser.ParsePakMeta(new MemoryStream(SyntheticPak.Build(("Mods/X/meta.lsx", Meta()))));

        meta.ScriptExtenderConfigs.Should().BeEmpty();
        meta.ScriptExtenderConfigMetadata.HasValue.Should().BeFalse();
    }
    
    
}
