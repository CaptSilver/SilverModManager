using System.Xml;
using FluentAssertions;
using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.GameInstalled;

public class ModsettingsDocumentTests(IFileSystem fs)
{
    private const string Synthetic = """
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
            <version major="4" minor="8" revision="0" build="400"/>
            <region id="ModuleSettings">
                <node id="root">
                    <children>
                        <node id="Mods">
                            <children>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="GustavX"/>
                                    <attribute id="MD5" type="LSString" value=""/>
                                    <attribute id="Name" type="LSString" value="GustavX"/>
                                    <attribute id="PublishHandle" type="uint64" value="0"/>
                                    <attribute id="UUID" type="guid" value="cb555efe-2d9e-131f-8195-a89329d218ea"/>
                                    <attribute id="Version64" type="int64" value="36028797018963968"/>
                                </node>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="HonourX"/>
                                    <attribute id="MD5" type="LSString" value=""/>
                                    <attribute id="Name" type="LSString" value="HonourX"/>
                                    <attribute id="PublishHandle" type="uint64" value="0"/>
                                    <attribute id="UUID" type="guid" value="9c0c9e0f-0a8b-4f7e-ab1f-cf6f5c3b8b00"/>
                                    <attribute id="Version64" type="int64" value="36028797018963968"/>
                                </node>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="Cool_Mod_9f8e7d6c"/>
                                    <attribute id="MD5" type="LSString" value="abc123"/>
                                    <attribute id="Name" type="LSString" value="Cool Mod"/>
                                    <attribute id="PublishHandle" type="uint64" value="1234567"/>
                                    <attribute id="UUID" type="guid" value="11111111-2222-3333-4444-555555555555"/>
                                    <attribute id="Version64" type="int64" value="36310271995674624"/>
                                </node>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="Local_Mod"/>
                                    <attribute id="MD5" type="LSString" value=""/>
                                    <attribute id="Name" type="LSString" value="Local Mod"/>
                                    <attribute id="PublishHandle" type="uint64" value="0"/>
                                    <attribute id="UUID" type="guid" value="66666666-7777-8888-9999-000000000000"/>
                                    <attribute id="Version64" type="int64" value="1"/>
                                </node>
                            </children>
                        </node>
                    </children>
                </node>
            </region>
        </save>
        """;

    [Fact]
    public void Parse_KeepsVersionAttributesVerbatim()
    {
        var doc = ModsettingsDocument.Parse(Synthetic);
        doc.VersionAttributes.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["major"] = "4", ["minor"] = "8", ["revision"] = "0", ["build"] = "400",
        });
    }

    [Fact]
    public void Parse_ReadsEveryEntryInFileOrder()
    {
        var doc = ModsettingsDocument.Parse(Synthetic);
        doc.Entries.Select(e => e.Folder).Should().Equal("GustavX", "HonourX", "Cool_Mod_9f8e7d6c", "Local_Mod");

        doc.Entries[2].Should().Be(new ModsettingsDocument.ModuleEntry(
            Folder: "Cool_Mod_9f8e7d6c",
            Name: "Cool Mod",
            PublishHandle: "1234567",
            Uuid: "11111111-2222-3333-4444-555555555555",
            Version64: "36310271995674624",
            Md5: "abc123"));
    }

    [Fact]
    public void BaseEntries_AreTheOnesWithoutAPublishHandle()
    {
        var doc = ModsettingsDocument.Parse(Synthetic);
        doc.BaseEntries.Select(e => e.Folder).Should().Equal("GustavX", "HonourX", "Local_Mod");
    }

    [Fact]
    public void BaseEntries_IncludeAnEntryWithNoPublishHandleAttribute()
    {
        var xml = Synthetic.Replace(
            """<attribute id="PublishHandle" type="uint64" value="1234567"/>""", "");
        var doc = ModsettingsDocument.Parse(xml);

        doc.Entries[2].PublishHandle.Should().BeEmpty();
        doc.Entries[2].IsPublished.Should().BeFalse();
        doc.BaseEntries.Select(e => e.Folder).Should().Equal("GustavX", "HonourX", "Cool_Mod_9f8e7d6c", "Local_Mod");
    }

    [Fact]
    public void Parse_ReadsTheShippedExample()
    {
        var path = fs.GetKnownPath(KnownPath.EntryDirectory)
            .Combine("BaldursGate3/Resources/ModsettingsFiles/example-modsettings-Patch7-Hotfix27.lsx");
        var doc = ModsettingsDocument.Parse(File.ReadAllText(path.ToString()));

        doc.VersionAttributes["major"].Should().Be("4");
        doc.VersionAttributes["build"].Should().Be("200");
        doc.Entries.Should().NotBeEmpty();
        // Unlike the load order reader, nothing is skipped: the first entry is GustavDev.
        doc.Entries[0].Folder.Should().Be("GustavDev");
        doc.Entries[0].Version64.Should().Be("36028797018963968");
        doc.Entries[1].Folder.Should().Be("ImpUI_26922ba9-6018-5252-075d-7ff2ba6ed879");
        doc.Entries[1].Md5.Should().Be("0f136f38f83bb9083fedcfb4a7b8510b");
    }

    [Fact]
    public void Parse_RejectsMalformedXml()
    {
        var act = () => ModsettingsDocument.Parse("<save><version");
        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void Parse_RejectsADocumentThatIsNotAModsettingsFile()
    {
        var act = () => ModsettingsDocument.Parse("<html></html>");
        act.Should().Throw<InvalidDataException>();
    }
}
