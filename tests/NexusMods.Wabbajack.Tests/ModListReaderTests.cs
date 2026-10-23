using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Tests.Fixtures;

namespace NexusMods.Wabbajack.Tests;

public class ModListReaderTests
{
    private const string HashA = "PfX6zen7m2M=";
    private const string HashB = "83fJglT61Dk=";

    private static ModList Read(string json) => ModListReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    [Fact]
    public void ReadsTheRealModlistFromTheWabbajackRepository()
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(WabbajackFixture.RealModlistPath);
        using var entry = zip.GetEntry("modlist")!.Open();

        var list = ModListReader.Read(entry);

        list.Name.Should().Be("MO2AndSKSETest");
        list.GameType.Should().Be("SkyrimSpecialEdition");
        list.Archives.Should().HaveCount(2);
        list.Archives.Select(a => a.State).Should().AllBeOfType<HttpState>();
        list.Archives[0].XxHash64.Should().Be(WabbajackHash.Parse(HashA));
        list.Archives[0].Name.Should().Be("Mod.Organizer-2.4.2.7z");
        list.Archives[0].Size.Should().Be(89503525);

        list.Directives.OfType<FromArchive>().Count(d => d.GetType() == typeof(FromArchive)).Should().Be(1395);
        list.Directives.Count(d => d.GetType() == typeof(InlineFile)).Should().Be(11);
        list.Directives.Count(d => d.GetType() == typeof(RemappedInlineFile)).Should().Be(1);
        list.Directives.Count(d => d.GetType() == typeof(ArchiveMeta)).Should().Be(2);
        list.Directives.Should().HaveCount(1409);

        var first = list.Directives.OfType<FromArchive>().First();
        first.Source.ArchiveXxHash64.Should().Be(WabbajackHash.Parse(HashA));
        first.Source.Parts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsEveryDirectiveKindAndDownloadStateUnderLongAndShortNames(bool longNames)
    {
        var list = Read(AllKindsJson(longNames));

        list.Name.Should().Be("All kinds");
        list.Author.Should().Be("me");
        list.Version.Should().Be("2.1.0.0");
        list.WabbajackVersion.Should().Be("4.3.2.1");
        list.GameType.Should().Be("Fallout4");
        list.OtherGames.Should().Equal("Fallout4VR");
        list.IsNsfw.Should().BeTrue();

        list.Archives.Select(a => a.State.GetType()).Should().Equal(
            typeof(NexusState), typeof(HttpState), typeof(WabbajackCdnState), typeof(GameFileSourceState),
            typeof(ManualState), typeof(MegaState), typeof(GoogleDriveState), typeof(MediaFireState),
            typeof(ModDbState), typeof(LoversLabState), typeof(VectorPlexusState), typeof(UnsupportedState),
            typeof(UnsupportedState), typeof(UnsupportedState));

        var states = list.Archives.Select(a => a.State).ToArray();
        states[0].Should().BeEquivalentTo(new NexusState("fallout4", 1234, 5678, "Mod", "1.0", "someone", true));
        states[1].Should().BeEquivalentTo(new HttpState(new Uri("https://example.org/a.7z"), ["Authorization: x"]));
        states[2].Should().Be(new WabbajackCdnState(new Uri("https://authored-files.wabbajack.org/a_1")));
        states[3].Should().Be(new GameFileSourceState("Fallout4", P("Data/Fallout4.esm"), WabbajackHash.Parse(HashB), "1.10.163.0"));
        states[4].Should().Be(new ManualState(new Uri("https://example.org/page"), "Click download"));
        states[5].Should().Be(new MegaState(new Uri("https://mega.nz/file/x")));
        states[6].Should().Be(new GoogleDriveState("drive-id"));
        states[7].Should().Be(new MediaFireState(new Uri("https://mediafire.com/x")));
        states[8].Should().Be(new ModDbState(new Uri("https://moddb.com/x")));
        states[9].Should().Be(new LoversLabState(new Uri("https://www.loverslab.com/files/file/1-x/?do=download"), "x.7z"));
        states[10].Should().Be(new VectorPlexusState(null, null));
        ((UnsupportedState)states[11]).TypeName.Should().Be("Bethesda");
        ((UnsupportedState)states[12]).TypeName.Should().Be("TESAllianceDownloader, Wabbajack.Lib");
        ((UnsupportedState)states[13]).TypeName.Should().Be("LoversLabDownloader, Wabbajack.Lib");

        var d = list.Directives;
        d.Select(x => x.GetType()).Should().Equal(
            typeof(FromArchive), typeof(FromArchive), typeof(PatchedFromArchive), typeof(TransformedTexture),
            typeof(InlineFile), typeof(RemappedInlineFile), typeof(PropertyFile), typeof(CreateBsa),
            typeof(MergedPatch), typeof(ArchiveMeta));

        var fromArchive = (FromArchive)d[0];
        fromArchive.To.Should().Be(P("mods/A/x.esp"));
        fromArchive.XxHash64.Should().Be(WabbajackHash.Parse(HashB));
        fromArchive.Size.Should().Be(10);
        fromArchive.Source.ArchiveXxHash64.Should().Be(WabbajackHash.Parse(HashA));
        fromArchive.Source.Parts.Should().Equal(P("Data/x.esp"), P("inner/y.bsa"));

        var zeroParts = (FromArchive)d[1];
        zeroParts.Source.Parts.Should().BeEmpty();
        zeroParts.Source.ArchiveXxHash64.Should().Be(WabbajackHash.Parse(HashA));

        var patched = (PatchedFromArchive)d[2];
        patched.FromHash.Should().Be(WabbajackHash.Parse(HashA));
        patched.PatchId.Should().Be("patch-1");
        patched.Source.Parts.Should().Equal(P("a.esp"));

        var texture = (TransformedTexture)d[3];
        texture.Image.Should().Be(new ImageState(512, 256, 5, "BC7_UNORM"));
        texture.Source.Parts.Should().Equal(P("t.dds"));

        ((InlineFile)d[4]).SourceDataId.Should().Be("blob-1");
        ((RemappedInlineFile)d[5]).SourceDataId.Should().Be("blob-2");
        var property = (PropertyFile)d[6];
        property.SourceDataId.Should().Be("blob-3");
        property.Kind.Should().Be(PropertyKind.Readme);

        var bsa = (CreateBsa)d[7];
        bsa.TempId.Should().Be(P("temp-1"));
        bsa.State.GetProperty("Magic").GetString().Should().Be("BSA\0");
        bsa.FileStates.Should().HaveCount(2);
        bsa.FileStates[1].GetProperty("Index").GetInt32().Should().Be(1);

        var merged = (MergedPatch)d[8];
        merged.PatchId.Should().Be("merge-1");
        merged.Sources.Should().Equal(
            new MergedPatchSource(WabbajackHash.Parse(HashA), P("mods/A/a.esp")),
            new MergedPatchSource(WabbajackHash.Parse(HashB), P("mods/B/b.esp")));

        ((ArchiveMeta)d[9]).SourceDataId.Should().Be("blob-4");
    }

    [Fact]
    public void CreateBsaStateOutlivesTheParsedDocument()
    {
        // The JsonElement is read after Read returned, so it must not point into a disposed document.
        var list = Read(AllKindsJson(longNames: true));
        GC.Collect();
        ((CreateBsa)list.Directives[7]).FileStates[0].GetProperty("Index").GetInt32().Should().Be(0);
    }

    [Fact]
    public void AnUnknownDirectiveTypeFailsTheLoadNamingIt()
    {
        var json = Json(archives: "", directives: """{ "$type": "NoMatch, Wabbajack.Lib", "To": "x", "Hash": "PfX6zen7m2M=", "Size": 1 }""");

        var act = () => Read(json);

        act.Should().Throw<UnsupportedModlistException>().Which.Message.Should().Contain("NoMatch, Wabbajack.Lib");
    }

    [Fact]
    public void AnUnknownDownloadStateBecomesUnsupportedAndKeepsItsUrl()
    {
        var json = Json(archives: $$"""{ "Hash": "{{HashA}}", "Size": 1, "Name": "a.7z", "State": { "$type": "SomethingNew, Wabbajack.Lib", "Url": "https://example.org/a" } }""", directives: "");

        var list = Read(json);

        list.Archives.Should().ContainSingle().Which.State.Should().Be(new UnsupportedState("SomethingNew, Wabbajack.Lib", "https://example.org/a"));
    }

    [Theory]
    [InlineData("""{ "$type": "FromArchive", "ArchiveHashPath": ["PfX6zen7m2M="], "Hash": "PfX6zen7m2M=", "Size": 1 }""", "To")]
    [InlineData("""{ "$type": "FromArchive", "To": "x", "Hash": "PfX6zen7m2M=", "Size": 1 }""", "ArchiveHashPath")]
    [InlineData("""{ "$type": "FromArchive", "To": "x", "ArchiveHashPath": [], "Hash": "PfX6zen7m2M=", "Size": 1 }""", "ArchiveHashPath")]
    [InlineData("""{ "$type": "InlineFile", "To": "x", "Hash": "PfX6zen7m2M=", "Size": 1 }""", "SourceDataID")]
    [InlineData("""{ "To": "x", "Hash": "PfX6zen7m2M=", "Size": 1 }""", "$type")]
    [InlineData("""{ "$type": "InlineFile", "To": "x", "Hash": "AAAA", "SourceDataID": "s", "Size": 1 }""", "Hash")]
    public void AMissingOrMalformedFieldFailsTheLoadNamingIt(string directive, string field)
    {
        var act = () => Read(Json(archives: "", directives: directive));

        act.Should().Throw<UnsupportedModlistException>().Which.Message.Should().Contain(field);
    }

    [Fact]
    public void AMissingTopLevelFieldFailsTheLoad()
    {
        var act = () => Read("""{ "Name": "x", "Archives": [] }""");

        act.Should().Throw<UnsupportedModlistException>();
    }

    [Fact]
    public void NotJsonFailsTheLoadWithTheSameException()
    {
        var act = () => Read("{ nope");

        act.Should().Throw<UnsupportedModlistException>();
    }

    [Theory]
    [InlineData(@"..\\..\\x", "x")]
    [InlineData(@"a\\..\\b\\c.esp", "b/c.esp")]
    [InlineData(@"a\\.\\b", "a/b")]
    [InlineData(@"a\\b\\..\\..\\..\\c", "c")]
    [InlineData(@"mods\\My Mod\\Data\\x.esp", "mods/My Mod/Data/x.esp")]
    public void PathsAreSplitOnBackslashesAndResolvedWithoutLeavingTheRoot(string raw, string expected)
    {
        var json = Json(archives: "", directives: $$"""{ "$type": "InlineFile", "To": "{{raw}}", "SourceDataID": "s", "Hash": "{{HashA}}", "Size": 1 }""");

        var list = Read(json);

        list.Directives.Should().ContainSingle().Which.To.Should().Be(P(expected));
    }

    [Fact]
    public void ArchiveHashPartsAreSanitizedLikeDirectivePaths()
    {
        var json = Json(archives: "", directives: $$"""{ "$type": "FromArchive", "To": "x", "ArchiveHashPath": ["{{HashA}}", "..\\..\\evil\\a.esp"], "Hash": "{{HashA}}", "Size": 1 }""");

        var list = Read(json);

        ((FromArchive)list.Directives.Single()).Source.Parts.Should().Equal(P("evil/a.esp"));
    }

    private static string Json(string archives, string directives) => $$"""
        { "Name": "n", "GameType": "SkyrimSpecialEdition", "Archives": [{{archives}}], "Directives": [{{directives}}] }
        """;

    private static string AllKindsJson(bool longNames)
    {
        var json = $$"""
        {
          "$type": "ModList, Wabbajack.Lib",
          "Name": "All kinds", "Author": "me", "Description": "d", "Version": "2.1.0.0", "WabbajackVersion": "4.3.2.1",
          "GameType": "Fallout4", "OtherGames": ["Fallout4VR"], "Image": "img.png", "Readme": "readme.md",
          "Website": null, "Community": "c", "IsNSFW": true,
          "Archives": [
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "Meta": "", "State": { "$type": "@Nexus@", "GameName": "fallout4", "ModID": 1234, "FileID": 5678, "Name": "Mod", "Version": "1.0", "Author": "someone", "IsNSFW": true } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@Http@", "Url": "https://example.org/a.7z", "Headers": ["Authorization: x"] } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@WabbajackCDN@", "Url": "https://authored-files.wabbajack.org/a_1" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@GameFileSource@", "Game": "Fallout4", "GameFile": "Data\\Fallout4.esm", "Hash": "{{HashB}}", "GameVersion": "1.10.163.0" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@Manual@", "Url": "https://example.org/page", "Prompt": "Click download" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@Mega@", "Url": "https://mega.nz/file/x" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@GoogleDrive@", "Id": "drive-id" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@MediaFire@", "Url": "https://mediafire.com/x" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@ModDB@", "Url": "https://moddb.com/x" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@LoversLab@", "IPS4Mod": 1, "IPS4File": "x.7z", "IPS4Url": "https://www.loverslab.com/files/file/1-x/?do=download", "IsAttachment": false } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "@VectorPlexus@", "IPS4Mod": 2 } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "Bethesda", "ProductId": 1 } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "TESAllianceDownloader, Wabbajack.Lib" } },
            { "Hash": "{{HashA}}", "Size": 1, "Name": "n", "State": { "$type": "LoversLabDownloader, Wabbajack.Lib" } }
          ],
          "Directives": [
            { "$type": "@FromArchive@", "To": "mods\\A\\x.esp", "Hash": "{{HashB}}", "Size": 10, "ArchiveHashPath": ["{{HashA}}", "Data\\x.esp", "inner\\y.bsa"] },
            { "$type": "@FromArchive@", "To": "mods\\A\\z.esp", "Hash": "{{HashB}}", "Size": 10, "ArchiveHashPath": ["{{HashA}}"] },
            { "$type": "@PatchedFromArchive@", "To": "mods\\A\\p.esp", "Hash": "{{HashB}}", "Size": 10, "ArchiveHashPath": ["{{HashA}}", "a.esp"], "FromHash": "{{HashA}}", "PatchID": "patch-1" },
            { "$type": "@TransformedTexture@", "To": "mods\\A\\t.dds", "Hash": "{{HashB}}", "Size": 10, "ArchiveHashPath": ["{{HashA}}", "t.dds"], "ImageState": { "Width": 512, "Height": 256, "MipLevels": 5, "Format": "BC7_UNORM", "PerceptualHash": { "Data": "AAAA" } } },
            { "$type": "@InlineFile@", "To": "i.txt", "Hash": "{{HashB}}", "Size": 3, "SourceDataID": "blob-1" },
            { "$type": "@RemappedInlineFile@", "To": "r.ini", "Hash": "{{HashB}}", "Size": 3, "SourceDataID": "blob-2" },
            { "$type": "@PropertyFile@", "To": "readme.md", "Hash": "{{HashB}}", "Size": 3, "SourceDataID": "blob-3", "Type": "Readme" },
            { "$type": "@CreateBSA@", "To": "mods\\A\\a.bsa", "Hash": "{{HashB}}", "Size": 3, "TempID": "temp-1",
              "State": { "$type": "BSAState, Compression.BSA", "Magic": "BSA\u0000", "Version": 105, "ArchiveFlags": 3, "FileFlags": 1 },
              "FileStates": [ { "$type": "BSAFileState, Compression.BSA", "Index": 0, "Path": "a.nif", "FlipCompression": false }, { "$type": "BSAFileState, Compression.BSA", "Index": 1, "Path": "b.nif", "FlipCompression": false } ] },
            { "$type": "@MergedPatch@", "To": "merged.esp", "Hash": "{{HashB}}", "Size": 3, "PatchID": "merge-1",
              "Sources": [ { "Hash": "{{HashA}}", "RelativePath": "mods\\A\\a.esp" }, { "Hash": "{{HashB}}", "RelativePath": "mods\\B\\b.esp" } ] },
            { "$type": "@ArchiveMeta@", "To": "a.7z.meta", "Hash": "{{HashB}}", "Size": 3, "SourceDataID": "blob-4" }
          ]
        }
        """;

        // Upstream's long names use ", Wabbajack.Lib" (CreateBSA and MergedPatch differ in which form is primary);
        // every name below is accepted in both forms.
        (string Token, string Long, string Short)[] names =
        [
            ("Nexus", "NexusDownloader, Wabbajack.Lib", "Nexus"),
            ("Http", "HttpDownloader, Wabbajack.Lib", "Http"),
            ("WabbajackCDN", "WabbajackCDNDownloader+State, Wabbajack.Lib", "WabbajackCDN"),
            ("GameFileSource", "GameFileSourceDownloader, Wabbajack.Lib", "GameFileSource"),
            ("Manual", "ManualDownloader, Wabbajack.Lib", "Manual"),
            ("Mega", "MegaDownloader, Wabbajack.Lib", "Mega"),
            ("GoogleDrive", "GoogleDriveDownloader, Wabbajack.Lib", "GoogleDrive"),
            ("MediaFire", "MediaFireDownloader+State, Wabbajack.Lib", "MediaFire"),
            ("ModDB", "ModDBDownloader, Wabbajack.Lib", "ModDB"),
            ("LoversLab", "LoversLabOAuthDownloader, Wabbajack.Lib", "LoversLab"),
            ("VectorPlexus", "VectorPlexusOAuthDownloader+State, Wabbajack.Lib", "VectorPlexus"),
            ("FromArchive", "FromArchive, Wabbajack.Lib", "FromArchive"),
            ("PatchedFromArchive", "PatchedFromArchive, Wabbajack.Lib", "PatchedFromArchive"),
            ("TransformedTexture", "TransformedTexture, Wabbajack.Lib", "TransformedTexture"),
            ("InlineFile", "InlineFile, Wabbajack.Lib", "InlineFile"),
            ("RemappedInlineFile", "RemappedInlineFile, Wabbajack.Lib", "RemappedInlineFile"),
            ("PropertyFile", "PropertyFile, Wabbajack.Lib", "PropertyFile"),
            ("CreateBSA", "CreateBSA, Wabbajack.Lib", "CreateBSA"),
            ("MergedPatch", "MergedPatch, Wabbajack.Lib", "MergedPatch"),
            ("ArchiveMeta", "ArchiveMeta, Wabbajack.Lib", "ArchiveMeta"),
        ];
        foreach (var (token, longName, shortName) in names)
            json = json.Replace($"@{token}@", longNames ? longName : shortName);
        return json;
    }
}
