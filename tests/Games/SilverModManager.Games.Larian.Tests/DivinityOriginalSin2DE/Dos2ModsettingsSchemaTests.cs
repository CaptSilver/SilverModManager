using FluentAssertions;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.Modsettings;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>The Divinity: Original Sin 2 dialect of <c>modsettings.lsx</c>: two lists, numeric type ids, an adventure block.</summary>
public class Dos2ModsettingsSchemaTests
{
    private const string Shared = "2bd9bdbe-22ae-4aa2-9c93-205880fc6564";
    private const string SharedDos = "eedf7638-36ff-4f26-a50a-076b87d53ba0";
    private const string Origins = "1301db3d-1f54-4e98-9be5-5094030916e4";
    private const string GiftBag = "9b45f7e5-d4e2-4fc2-8ef7-3b8e90a5256c";
    private const string AppPak = "a1a1a1a1-0000-4000-8000-000000000001";
    private const string Campaign = "c4c4c4c4-0000-4000-8000-000000000004";
    private const string CampaignDep = "d5d5d5d5-0000-4000-8000-000000000005";

    private static readonly Dos2ModsettingsSchema Schema = new();

    private static ModsettingsDocument.ModuleEntry Entry(string uuid, string name) =>
        new(Folder: name, Name: name, PublishHandle: "", Uuid: uuid, Version64: "268435456", Md5: "");

    private static ModsettingsDocument DocumentOf(params ModsettingsDocument.ModuleEntry[] entries) =>
        Schema.Parse(Schema.Render(Schema.DefaultVersion, entries, layoutFrom: null));

    // Written the way the renderer lays it out, which is how the game's own file reads.
    private static string File(string orderUuids, string mods, string version = """<version major="3" minor="6" revision="9" build="0" />""") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <save>
          <header version="2" />
          {{version}}
          <region id="ModuleSettings">
            <node id="root">
              <children>
                <node id="ModOrder">
                  <children>
        {{orderUuids}}
                  </children>
                </node>
                <node id="Mods">
                  <children>
        {{mods}}
                  </children>
                </node>
              </children>
            </node>
          </region>
        </save>
        """.ReplaceLineEndings("\n");

    private static string OrderNode(string uuid, string type = "22") => $$"""
                    <node id="Module">
                      <attribute id="UUID" value="{{uuid}}" type="{{type}}" />
                    </node>
        """.ReplaceLineEndings("\n");

    private static string ModNode(string folder, string name, string uuid, string version = "268435456", string versionType = "4", string md5 = "") => $$"""
                    <node id="ModuleShortDesc">
                      <attribute id="Folder" value="{{folder}}" type="30" />
                      <attribute id="MD5" value="{{md5}}" type="23" />
                      <attribute id="Name" value="{{name}}" type="22" />
                      <attribute id="UUID" value="{{uuid}}" type="22" />
                      <attribute id="Version" value="{{version}}" type="{{versionType}}" />
                    </node>
        """.ReplaceLineEndings("\n");

    private static string Lines(params string[] nodes) => string.Join("\n", nodes);

    [Fact]
    public void ParseThenRenderIsByteStable()
    {
        var text = File(
            Lines(OrderNode(Shared), OrderNode(AppPak)),
            Lines(ModNode("Shared", "Shared", Shared, md5: "abc"), ModNode("AppPak", "App Pak", AppPak, "369098753")));

        var document = Schema.Parse(text);

        document.Entries.Select(e => e.Uuid).Should().Equal(Shared, AppPak);
        Schema.Render(document.VersionAttributes, document.Entries, document).Should().Be(text);
    }

    [Fact]
    public void AByteOrderMarkAndWindowsLineEndingsCarryOver()
    {
        var text = "﻿" + File(OrderNode(Shared), ModNode("Shared", "Shared", Shared)).Replace("\n", "\r\n");

        var document = Schema.Parse(text);

        document.HasBom.Should().BeTrue();
        document.LineEnding.Should().Be("\r\n");
        Schema.Render(document.VersionAttributes, document.Entries, document).Should().Be(text);
    }

    [Fact]
    public void ModOrderAndModsAreWrittenInTheSameOrder()
    {
        var rendered = Schema.Render(Schema.DefaultVersion, [Entry(Shared, "Shared"), Entry(AppPak, "App Pak"), Entry(GiftBag, "Gift")], null);

        var document = Schema.Parse(rendered);

        document.OrderUuids.Should().Equal(Shared, AppPak, GiftBag);
        document.Entries.Select(e => e.Uuid).Should().Equal(Shared, AppPak, GiftBag);
    }

    [Fact]
    public void ADefaultFileSaysVersion3_6_9_0AndHasADocumentHeader()
    {
        var rendered = Schema.Render(Schema.DefaultVersion, [Entry(Shared, "Shared")], null);

        rendered.Should().Contain("""<header version="2" />""").And.Contain("""<version major="3" minor="6" revision="9" build="0" />""");
        rendered.Should().Contain("""<attribute id="Version" value="268435456" type="4" />""");
        rendered.Should().Contain("""<attribute id="Folder" value="Shared" type="30" />""");
    }

    [Fact]
    public void ModsIsTheOrderWhenModOrderDisagrees()
    {
        var text = File(
            Lines(OrderNode(AppPak), OrderNode(Shared)),
            Lines(ModNode("Shared", "Shared", Shared), ModNode("AppPak", "App Pak", AppPak)));

        var document = Schema.Parse(text);

        document.Entries.Select(e => e.Uuid).Should().Equal(Shared, AppPak);
        document.OrderUuids.Should().Equal(AppPak, Shared);
    }

    [Fact]
    public void TheAdventureBlockStaysFirst()
    {
        var document = DocumentOf(Entry(CampaignDep, "DepOfAdventure"), Entry(Campaign, "Campaign"), Entry(AppPak, "ModA"));

        Schema.GameEntries(document, new HashSet<string> { Campaign }).Select(e => e.Uuid).Should().Equal(CampaignDep, Campaign);
    }

    [Fact]
    public void TheGamesOwnAdventuresAreKeptWithNoAdventureInTheLoadout()
    {
        var document = DocumentOf(Entry(Shared, "Shared"), Entry(SharedDos, "Shared_DOS"), Entry(Origins, "Origins"), Entry(AppPak, "ModA"));

        Schema.GameEntries(document, new HashSet<string>()).Select(e => e.Uuid).Should().Equal(Shared, SharedDos, Origins);
    }

    [Fact]
    public void GameModulesAndGiftBagsSurviveAWriteInTheirDiskPositions()
    {
        var disk = DocumentOf(Entry(Shared, "Shared"), Entry(SharedDos, "Shared_DOS"), Entry(Origins, "Origins"), Entry(AppPak, "AppPak"), Entry(GiftBag, "GiftBag"));

        var written = Schema.GameEntries(disk, new HashSet<string>()).Concat([Entry(AppPak, "AppPak")]);

        Schema.Parse(Schema.Render(disk.VersionAttributes, written, disk)).Entries.Select(e => e.Name)
            .Should().Equal("Shared", "Shared_DOS", "Origins", "GiftBag", "AppPak");
    }

    [Fact]
    public void AModTheUserEnabledInGameIsNotTheGamesToKeep()
    {
        var document = DocumentOf(Entry(Shared, "Shared"), Entry(AppPak, "ModA"));

        Schema.GameEntries(document, new HashSet<string>()).Select(e => e.Uuid).Should().Equal(Shared);
    }

    [Fact]
    public void NumericTypeIdsAreKeptFromTheFile()
    {
        var text = File(OrderNode(AppPak), ModNode("AppPak", "App Pak", AppPak, versionType: "int32"));

        var document = Schema.Parse(text);

        document.Entries.Single().AttributeTypes.Should().ContainKey("Version").WhoseValue.Should().Be("int32");
        Schema.Render(document.VersionAttributes, document.Entries, document).Should().Contain("""<attribute id="Version" value="268435456" type="int32" />""");
    }

    [Fact]
    public void ANewEntryTakesTheDefaultTypeIds()
    {
        var rendered = Schema.Render(Schema.DefaultVersion, [Entry(AppPak, "App Pak")], null);

        rendered.Should().Contain("""<attribute id="MD5" value="" type="23" />""").And.Contain("""<attribute id="Name" value="App Pak" type="22" />""");
    }

    [Fact]
    public void CannotBuildAFileFromNothing()
    {
        Schema.CanCreateFromNothing.Should().BeFalse();
        Schema.EntriesWhenNoFile.Should().BeEmpty();
    }

    [Fact]
    public void ATextWithNoSaveElementIsRefused()
    {
        var parse = () => Schema.Parse("<other />");

        parse.Should().Throw<InvalidDataException>();
    }
}
