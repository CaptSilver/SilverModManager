using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Unreal.Tests;

/// <summary>
/// The layout of a made-up Unreal game, "Fake", whose project folder sits under the game folder. Nothing here
/// knows about any real title.
/// </summary>
public class UnrealModLayoutTests
{
    private static readonly UnrealModLayout Layout = UnrealModLayout.ForProject(new GamePath(LocationId.Game, "Fake"));

    private static string? Route(string path) =>
        Layout.TryRoute(RelativePath.FromUnsanitizedInput(path), out var target) ? $"{target.LocationId}/{target.Path}" : null;

    [Theory]
    [InlineData("Mod_P.pak")]
    [InlineData("Mod_P.ucas")]
    [InlineData("Mod_P.utoc")]
    [InlineData("Wrapper/Mod_P.pak")]
    [InlineData("Fake/Content/Paks/Mod_P.pak")]
    [InlineData("Paks/Mod_P.pak")]
    [InlineData("~mods/Mod_P.pak")]
    [InlineData("Paks/~mods/Mod_P.pak")]
    public void PakFamilyFilesLandFlatInTheModsFolder(string path) =>
        Route(path).Should().Be("Game/Fake/Content/Paks/~mods/Mod_P" + Path.GetExtension(path));

    [Fact]
    public void ThePakTripleStaysTogetherInASubfolder()
    {
        string[] files = ["~mods/Group/Mod_P.pak", "~mods/Group/Mod_P.ucas", "~mods/Group/Mod_P.utoc"];

        files.Select(Route).Should().Equal(
            "Game/Fake/Content/Paks/~mods/Group/Mod_P.pak",
            "Game/Fake/Content/Paks/~mods/Group/Mod_P.ucas",
            "Game/Fake/Content/Paks/~mods/Group/Mod_P.utoc");
    }

    [Fact]
    public void ThePakFolderNameIsMatchedWithoutRegardToCase() =>
        Route("PAKS/~MODS/Group/Mod_P.pak").Should().Be("Game/Fake/Content/Paks/~mods/Group/Mod_P.pak");

    [Theory]
    [InlineData("LogicMods/Blueprint.pak", "Game/Fake/Content/Paks/LogicMods/Blueprint.pak")]
    [InlineData("Paks/LogicMods/Blueprint.pak", "Game/Fake/Content/Paks/LogicMods/Blueprint.pak")]
    [InlineData("Wrapper/LogicMods/Blueprint.pak", "Game/Fake/Content/Paks/LogicMods/Blueprint.pak")]
    [InlineData("Fake/Content/Paks/LogicMods/Blueprint.utoc", "Game/Fake/Content/Paks/LogicMods/Blueprint.utoc")]
    public void BlueprintModsLandInLogicMods(string path, string expected) => Route(path).Should().Be(expected);

    [Theory]
    [InlineData("dwmapi.dll")]
    [InlineData("UE4SS.dll")]
    [InlineData("UE4SS-settings.ini")]
    [InlineData("Wrapper/dwmapi.dll")]
    public void TheUe4ssLoaderLandsNextToTheGameBinary(string path) =>
        Route(path).Should().Be("Game/Fake/Binaries/Win64/" + Path.GetFileName(path));

    [Theory]
    [InlineData("ue4ss/UE4SS.dll", "Game/Fake/Binaries/Win64/ue4ss/UE4SS.dll")]
    [InlineData("ue4ss/UE4SS-settings.ini", "Game/Fake/Binaries/Win64/ue4ss/UE4SS-settings.ini")]
    [InlineData("ue4ss/Mods/mods.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/mods.txt")]
    public void ALoaderPackedInItsOwnFolderKeepsThatFolder(string path, string expected) => Route(path).Should().Be(expected);

    [Theory]
    [InlineData("MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua")]
    [InlineData("MyMod/Scripts/lib/util.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/lib/util.lua")]
    [InlineData("MyMod/dlls/main.dll", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/dlls/main.dll")]
    [InlineData("MyMod/enabled.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt")]
    [InlineData("Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua")]
    [InlineData("ue4ss/Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua")]
    [InlineData("Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt")]
    [InlineData("Wrapper/Mods/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt")]
    [InlineData("Wrapper/Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua")]
    public void AUe4ssModLandsInItsOwnFolderUnderMods(string path, string expected) => Route(path).Should().Be(expected);

    [Theory]
    [InlineData("Binaries/Win64/tool.exe", "Game/Fake/Binaries/Win64/tool.exe")]
    [InlineData("Fake/Binaries/Win64/OBSE/Plugins/p.dll", "Game/Fake/Binaries/Win64/OBSE/Plugins/p.dll")]
    public void FilesPackedFromTheBinariesFolderKeepTheirPlaceBelowIt(string path, string expected) => Route(path).Should().Be(expected);

    [Theory]
    [InlineData("Data/Mod.esp")]
    [InlineData("textures/t.dds")]
    [InlineData("Scripts/readme.txt")]
    [InlineData("Wrapper/Scripts/compiled.pex")]
    [InlineData("textures/mods/x.dds")]
    [InlineData("Wrapper/meshes/Mods/x.nif")]
    [InlineData("Mod.esp")]
    [InlineData("readme.txt")]
    public void FilesItDoesNotRecogniseAreLeftToTheGame(string path) => Route(path).Should().BeNull();

    private static string? RouteWith(Ue4ssLayout ue4ss, string path) =>
        UnrealModLayout.ForProject(new GamePath(LocationId.Game, "Fake"), ue4ss)
            .TryRoute(RelativePath.FromUnsanitizedInput(path), out var target) ? $"{target.LocationId}/{target.Path}" : null;

    // One row per archive shape. The UE4SS 3.x column is what every game gets by default; the 2.x column keeps mods in
    // Mods beside the binary, and anything packed under ue4ss/ keeps the 3.x place whichever layout the game has.
    public static TheoryData<Ue4ssLayout, string, string> ArchiveShapes => new()
    {
        // Pak with its IoStore companions.
        { Ue4ssLayout.Ue4ss3, "Mod_P.pak", "Game/Fake/Content/Paks/~mods/Mod_P.pak" },
        { Ue4ssLayout.Ue4ss3, "Mod_P.utoc", "Game/Fake/Content/Paks/~mods/Mod_P.utoc" },
        { Ue4ssLayout.Ue4ss3, "Mod_P.ucas", "Game/Fake/Content/Paks/~mods/Mod_P.ucas" },
        { Ue4ssLayout.Ue4ss2, "Mod_P.pak", "Game/Fake/Content/Paks/~mods/Mod_P.pak" },
        { Ue4ssLayout.Ue4ss2, "Mod_P.utoc", "Game/Fake/Content/Paks/~mods/Mod_P.utoc" },
        { Ue4ssLayout.Ue4ss2, "Mod_P.ucas", "Game/Fake/Content/Paks/~mods/Mod_P.ucas" },
        // Blueprint mod.
        { Ue4ssLayout.Ue4ss3, "LogicMods/Blueprint.pak", "Game/Fake/Content/Paks/LogicMods/Blueprint.pak" },
        { Ue4ssLayout.Ue4ss2, "LogicMods/Blueprint.pak", "Game/Fake/Content/Paks/LogicMods/Blueprint.pak" },
        // A UE4SS mod in a Mods folder at the archive root.
        { Ue4ssLayout.Ue4ss3, "Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua" },
        { Ue4ssLayout.Ue4ss2, "Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/Mods/MyMod/Scripts/main.lua" },
        // A UE4SS mod found by its Scripts, dlls or enabled.txt.
        { Ue4ssLayout.Ue4ss3, "MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua" },
        { Ue4ssLayout.Ue4ss2, "MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/Mods/MyMod/Scripts/main.lua" },
        { Ue4ssLayout.Ue4ss3, "MyMod/dlls/main.dll", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/dlls/main.dll" },
        { Ue4ssLayout.Ue4ss2, "MyMod/dlls/main.dll", "Game/Fake/Binaries/Win64/Mods/MyMod/dlls/main.dll" },
        { Ue4ssLayout.Ue4ss3, "Wrapper/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt" },
        { Ue4ssLayout.Ue4ss2, "Wrapper/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/Mods/MyMod/enabled.txt" },
        // The loader sits beside the binary under both.
        { Ue4ssLayout.Ue4ss3, "dwmapi.dll", "Game/Fake/Binaries/Win64/dwmapi.dll" },
        { Ue4ssLayout.Ue4ss2, "dwmapi.dll", "Game/Fake/Binaries/Win64/dwmapi.dll" },
        // 2.x loads through xinput1_3.dll; 3.x moved to dwmapi.dll and does not claim the name.
        { Ue4ssLayout.Ue4ss2, "xinput1_3.dll", "Game/Fake/Binaries/Win64/xinput1_3.dll" },
        { Ue4ssLayout.Ue4ss2, "Mods/mods.txt", "Game/Fake/Binaries/Win64/Mods/mods.txt" },
        { Ue4ssLayout.Ue4ss3, "UE4SS.dll", "Game/Fake/Binaries/Win64/UE4SS.dll" },
        { Ue4ssLayout.Ue4ss2, "UE4SS.dll", "Game/Fake/Binaries/Win64/UE4SS.dll" },
        { Ue4ssLayout.Ue4ss3, "UE4SS-settings.ini", "Game/Fake/Binaries/Win64/UE4SS-settings.ini" },
        { Ue4ssLayout.Ue4ss2, "UE4SS-settings.ini", "Game/Fake/Binaries/Win64/UE4SS-settings.ini" },
        // An archive that packs ue4ss/ follows itself.
        { Ue4ssLayout.Ue4ss3, "ue4ss/UE4SS.dll", "Game/Fake/Binaries/Win64/ue4ss/UE4SS.dll" },
        { Ue4ssLayout.Ue4ss2, "ue4ss/UE4SS.dll", "Game/Fake/Binaries/Win64/ue4ss/UE4SS.dll" },
        { Ue4ssLayout.Ue4ss3, "ue4ss/Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua" },
        { Ue4ssLayout.Ue4ss2, "ue4ss/Mods/MyMod/Scripts/main.lua", "Game/Fake/Binaries/Win64/ue4ss/Mods/MyMod/Scripts/main.lua" },
        { Ue4ssLayout.Ue4ss3, "ue4ss/Mods/mods.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/mods.txt" },
        { Ue4ssLayout.Ue4ss2, "ue4ss/Mods/mods.txt", "Game/Fake/Binaries/Win64/ue4ss/Mods/mods.txt" },
        // Packed from the game's own Binaries folder: kept as is.
        { Ue4ssLayout.Ue4ss2, "Fake/Binaries/Win64/Mods/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/Mods/MyMod/enabled.txt" },
        { Ue4ssLayout.Ue4ss3, "Fake/Binaries/Win64/Mods/MyMod/enabled.txt", "Game/Fake/Binaries/Win64/Mods/MyMod/enabled.txt" },
    };

    [Fact]
    public void TheUe4ss3LayoutDoesNotClaimTheXinputProxyOfUe4ss2() =>
        RouteWith(Ue4ssLayout.Ue4ss3, "xinput1_3.dll").Should().BeNull();

    [Theory]
    [MemberData(nameof(ArchiveShapes))]
    public void EachArchiveShapeLandsWhereTheUe4ssLayoutPutsIt(Ue4ssLayout ue4ss, string path, string expected) =>
        RouteWith(ue4ss, path).Should().Be(expected);

    [Theory]
    [InlineData(Ue4ssLayout.Ue4ss3)]
    [InlineData(Ue4ssLayout.Ue4ss2)]
    public void FilesItDoesNotRecogniseAreLeftToTheGameUnderEitherUe4ssLayout(Ue4ssLayout ue4ss)
    {
        RouteWith(ue4ss, "textures/mods/x.dds").Should().BeNull();
        RouteWith(ue4ss, "Wrapper/Scripts/compiled.pex").Should().BeNull();
    }

    [Fact]
    public void TheDefaultLayoutIsUe4ss3()
    {
        var layout = new UnrealModLayout(new GamePath(LocationId.Game, "Fake/Content"), new GamePath(LocationId.Game, "Fake/Binaries/Win64"));

        layout.TryRoute("MyMod/enabled.txt", out var target).Should().BeTrue();
        target.Should().Be(new GamePath(LocationId.Game, "Fake/Binaries/Win64/ue4ss/Mods/MyMod/enabled.txt"));
    }

    [Fact]
    public void TheRootsComeFromTheConstructor()
    {
        var layout = new UnrealModLayout(new GamePath(LocationId.Game, "Other/Content"), new GamePath(LocationId.Game, "Other/Binaries/WinGDK"));

        layout.TryRoute("Mod_P.pak", out var pak).Should().BeTrue();
        layout.TryRoute("dwmapi.dll", out var loader).Should().BeTrue();

        pak.Should().Be(new GamePath(LocationId.Game, "Other/Content/Paks/~mods/Mod_P.pak"));
        loader.Should().Be(new GamePath(LocationId.Game, "Other/Binaries/WinGDK/dwmapi.dll"));
    }
}
