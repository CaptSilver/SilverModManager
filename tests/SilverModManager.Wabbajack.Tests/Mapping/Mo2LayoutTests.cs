using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Wabbajack.Mapping;

namespace NexusMods.Wabbajack.Tests.Mapping;

public class Mo2LayoutTests
{
    private static readonly GamePath SkyrimIni = new(LocationId.Preferences, "Skyrim.ini");
    private static readonly GamePath SkyrimPrefs = new(LocationId.Preferences, "SkyrimPrefs.ini");

    private static readonly Mo2Context Context = new(
        "Main",
        new HashSet<string>(["Stock Game"]),
        [SkyrimIni, SkyrimPrefs],
        rel => new GamePath(LocationId.Game, "Data") / rel);

    private static Mo2Target Classify(string to, Mo2Context? context = null) =>
        Mo2Layout.Classify(RelativePath.FromUnsanitizedInput(to), context ?? Context);

    [Fact]
    public void ModOrganizerIni_IsASeed_AndNotDropped()
    {
        Classify("ModOrganizer.ini").Should().Be(new Mo2Target.Seed(SeedKind.ModOrganizerIni));
    }

    [Theory]
    [InlineData("ModOrganizer.exe")]
    [InlineData("nxmhandler.exe")]
    [InlineData("nxmhandler.ini")]
    [InlineData("nxmhandler.log")]
    [InlineData("pythoncore.zip")]
    [InlineData("dump_running_process.bat")]
    [InlineData("MO2AndSKSETest.compiler_settings")]
    [InlineData("MO2AndSKSETest.mo2_compiler_settings")]
    [InlineData("something.meta")]
    [InlineData("uibase.dll")]
    [InlineData("helper.exe")]
    [InlineData("portable.txt")]
    public void AnyOtherFileAtTheRoot_IsDropped(string name)
    {
        Classify(name).Should().Be(new Mo2Target.Dropped("Mod Organizer file"));
    }

    [Fact]
    public void ModMetadata_IsDropped()
    {
        Classify("mods/Some Mod/meta.ini").Should().BeOfType<Mo2Target.Dropped>();
    }

    [Fact]
    public void AModFile_GoesWhereTheGameSaysModFilesGo()
    {
        Classify("mods/Some Mod/meshes/a.nif")
            .Should().Be(new Mo2Target.ModFile("Some Mod", new GamePath(LocationId.Game, "Data/meshes/a.nif")));
    }

    [Fact]
    public void ARootFolderUnderAMod_GoesToTheGameFolder()
    {
        Classify("mods/Some Mod/Root/skse64_loader.exe")
            .Should().Be(new Mo2Target.ModFile("Some Mod", new GamePath(LocationId.Game, "skse64_loader.exe")));
    }

    [Fact]
    public void FolderNamesAreMatchedWithoutRegardToCase()
    {
        Classify("Mods/Some Mod/ROOT/x.dll")
            .Should().Be(new Mo2Target.ModFile("Some Mod", new GamePath(LocationId.Game, "x.dll")));
        Classify("MODS/Some Mod/META.INI").Should().BeOfType<Mo2Target.Dropped>();
        Classify("Profiles/main/MODLIST.TXT").Should().Be(new Mo2Target.Seed(SeedKind.ModlistTxt));
    }

    [Fact]
    public void Overwrite_IsAModGroupWithTheSameRouting()
    {
        Classify("overwrite/SKSE/Plugins/x.json")
            .Should().Be(new Mo2Target.ModFile("Overwrite", new GamePath(LocationId.Game, "Data/SKSE/Plugins/x.json")));
        Classify("overwrite/Root/x.ini")
            .Should().Be(new Mo2Target.ModFile("Overwrite", new GamePath(LocationId.Game, "x.ini")));
    }

    [Theory]
    [InlineData("profiles/Main/modlist.txt", SeedKind.ModlistTxt)]
    [InlineData("profiles/Main/plugins.txt", SeedKind.PluginsTxt)]
    [InlineData("profiles/Main/loadorder.txt", SeedKind.LoadorderTxt)]
    [InlineData("profiles/main/Loadorder.TXT", SeedKind.LoadorderTxt)]
    public void TheSelectedProfilesOrderingFiles_AreSeeds(string to, SeedKind kind)
    {
        Classify(to).Should().Be(new Mo2Target.Seed(kind));
    }

    [Fact]
    public void AProfileIni_MapsToTheGamesCanonicalSpelling()
    {
        // GamePath equality ignores case, so the spelling is checked on the text.
        var prefs = Classify("profiles/Main/skyrimprefs.ini").Should().BeOfType<Mo2Target.ProfileSetting>().Subject;
        prefs.Path.LocationId.Should().Be(SkyrimPrefs.LocationId);
        prefs.Path.Path.ToString().Should().Be("SkyrimPrefs.ini");

        var ini = Classify("profiles/Main/SKYRIM.INI").Should().BeOfType<Mo2Target.ProfileSetting>().Subject;
        ini.Path.Path.ToString().Should().Be(SkyrimIni.Path.ToString());
    }

    [Theory]
    [InlineData("profiles/Main/settings.ini")]
    [InlineData("profiles/Main/archives.txt")]
    [InlineData("profiles/Main/lockedorder.txt")]
    [InlineData("profiles/Main/initweaks.ini")]
    [InlineData("profiles/Main/saves/slot1.ess")]
    public void OtherFilesOfTheSelectedProfile_AreDropped(string to)
    {
        Classify(to).Should().Be(new Mo2Target.Dropped("profile file"));
    }

    [Fact]
    public void AnotherProfile_IsDropped_EvenForItsOrderingFiles()
    {
        Classify("profiles/Other/modlist.txt").Should().Be(new Mo2Target.Dropped("other profile"));
        Classify("profiles/Other/skyrim.ini").Should().Be(new Mo2Target.Dropped("other profile"));
    }

    [Fact]
    public void TempBsaFiles_AreInputsToTheArchiveWithThatId()
    {
        Classify("TEMP_BSA_FILES/abc123/meshes/a.nif")
            .Should().Be(new Mo2Target.BsaInput(RelativePath.FromUnsanitizedInput("abc123"), RelativePath.FromUnsanitizedInput("meshes/a.nif")));
    }

    [Fact]
    public void AStockGameFolder_AndGameFolderFiles_AreGameRootFiles()
    {
        Classify("Stock Game/Data/Skyrim.esm").Should().Be(new Mo2Target.GameRootFile("Stock Game", RelativePath.FromUnsanitizedInput("Data/Skyrim.esm")));
        Classify("stock game/d3dx9_42.dll").Should().Be(new Mo2Target.GameRootFile("Stock Game", RelativePath.FromUnsanitizedInput("d3dx9_42.dll")));
        Classify("Game Folder Files/enbseries.ini").Should().Be(new Mo2Target.GameRootFile("Game Folder Files", RelativePath.FromUnsanitizedInput("enbseries.ini")));
    }

    [Theory]
    [InlineData("dlls/Qt5Core.dll")]
    [InlineData("plugins/data/x.py")]
    [InlineData("stylesheets/a.qss")]
    [InlineData("translations/a.qm")]
    [InlineData("tutorials/a.js")]
    [InlineData("licenses/a.txt")]
    [InlineData("QtQml/a.qml")]
    [InlineData("QtQuick.2/a.qml")]
    [InlineData("platforms/qwindows.dll")]
    [InlineData("styles/a.dll")]
    [InlineData("pythoncore/a.py")]
    [InlineData("resources/a.png")]
    [InlineData("NCC/a.dll")]
    [InlineData("explorer++/Explorer++.exe")]
    [InlineData("loot/a.dll")]
    [InlineData("webcache/a")]
    [InlineData("logs/a.log")]
    [InlineData("downloads/a.7z")]
    public void ModOrganizersOwnFolders_AreDropped(string to)
    {
        Classify(to).Should().Be(new Mo2Target.Dropped("Mod Organizer"));
    }

    [Fact]
    public void AMetaFileInAnUnknownFolder_IsDropped()
    {
        Classify("Tools/xEdit/a.meta").Should().Be(new Mo2Target.Dropped("download metadata"));
    }

    [Fact]
    public void AnyOtherTopLevelFolder_IsAnExtra()
    {
        Classify("Tools/xEdit/xEdit.exe").Should().Be(new Mo2Target.Extra(RelativePath.FromUnsanitizedInput("Tools/xEdit/xEdit.exe")));
        Classify("DynDOLOD/a.txt").Should().BeOfType<Mo2Target.Extra>();
    }

    [Fact]
    public void AGameRootFolder_WinsOverTheMo2FolderList()
    {
        var context = Context with { GameRootFolders = new HashSet<string>(["plugins"]) };

        Classify("plugins/x.dll", context).Should().Be(new Mo2Target.GameRootFile("plugins", RelativePath.FromUnsanitizedInput("x.dll")));
    }
}
