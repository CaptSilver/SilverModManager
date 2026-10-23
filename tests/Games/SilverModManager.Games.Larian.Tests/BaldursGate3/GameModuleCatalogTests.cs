using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Games.Larian.BaldursGate3.Utils;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

public class GameModuleCatalogTests(TemporaryFileManager temporaryFileManager) : IDisposable
{
    private const string GustavUuid = "991c9c7a-fb80-40cb-8f0d-b92d4e80e9b1";
    private const string PhotoModeUuid = "b3a0c7a4-1ae6-4a7d-9d52-1f4b1c4c2f10";
    private const string CrossplayUuid = "2f6f8f7e-0a5b-4cf2-8f62-9c6a3ab3c1d9";

    private readonly TemporaryPath _root = temporaryFileManager.CreateFolder("bg3-game-modules");
    private AbsolutePath DataFolder => _root.Path.Combine("Data");

    public void Dispose() => _root.Dispose();

    private static GameModuleCatalog CreateCatalog() => new(NullLogger<GameModuleCatalog>.Instance);

    private void WritePak(string fileName, byte[] bytes)
    {
        var path = DataFolder.Combine(fileName);
        path.Parent.CreateDirectory();
        path.WriteAllBytesAsync(bytes).GetAwaiter().GetResult();
    }

    [Fact]
    public void ListsEveryModuleASetOfPaksHolds()
    {
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4, 1, 1)));
        // One pak can hold more than one module.
        WritePak("Shared.pak", SyntheticPak.Build(
            ("Mods/PhotoMode/meta.lsx", SyntheticPak.MetaLsx("PhotoMode", "PhotoMode", PhotoModeUuid, SyntheticPak.Version(1))),
            ("Mods/CrossplayUI/meta.lsx", SyntheticPak.MetaLsx("CrossplayUI", "CrossplayUI", CrossplayUuid, SyntheticPak.Version(2))),
            ("Public/PhotoMode/Assets/readme.txt", "not a module")
        ));

        var modules = CreateCatalog().GetModules(DataFolder);

        modules.Should().NotBeNull();
        modules!.Keys.Should().BeEquivalentTo(GustavUuid, PhotoModeUuid, CrossplayUuid);
        modules[CrossplayUuid].Name.Should().Be("CrossplayUI");
        modules[GustavUuid].SemanticVersion.ToString().Should().Be("4.1.1");
    }

    [Fact]
    public void PaksWithoutAModuleAreSkipped()
    {
        WritePak("Textures.pak", SyntheticPak.Build(("Public/Textures/a.dds", "data")));
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));

        CreateCatalog().GetModules(DataFolder)!.Keys.Should().BeEquivalentTo(GustavUuid);
    }

    [Fact]
    public void ACorruptPakDoesNotHideTheModulesOfTheOthers()
    {
        WritePak("Broken.pak", [1, 2, 3, 4, 5]);
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));

        CreateCatalog().GetModules(DataFolder)!.Keys.Should().BeEquivalentTo(GustavUuid);
    }

    [Fact]
    public void ThePartsOfASplitPakAreNotOpened()
    {
        // Textures_1.pak continues Textures.pak's data and has no header of its own.
        WritePak("Textures.pak", SyntheticPak.Build(("Public/Textures/a.dds", "data")));
        WritePak("Textures_1.pak", [9, 9, 9, 9, 9, 9, 9, 9]);
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));
        var catalog = CreateCatalog();

        catalog.GetModules(DataFolder)!.Keys.Should().BeEquivalentTo(GustavUuid);
        catalog.PakReads.Should().Be(2);
    }

    [Fact]
    public void AModuleWithMalformedMetaDoesNotHideTheOthers()
    {
        WritePak("Broken.pak", SyntheticPak.Build(("Mods/Broken/meta.lsx", "<save><region id=\"Config\">")));
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));

        CreateCatalog().GetModules(DataFolder)!.Keys.Should().BeEquivalentTo(GustavUuid);
    }

    [Fact]
    public void AFolderThatCannotBeReadHasNoModules()
    {
        CreateCatalog().GetModules(DataFolder).Should().BeNull("there is no Data folder");

        WritePak("Textures.pak", SyntheticPak.Build(("Public/Textures/a.dds", "data")));
        CreateCatalog().GetModules(DataFolder).Should().BeNull("no pak holds a module");
    }

    [Fact]
    public void ASecondCallReadsNothing()
    {
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));
        WritePak("Shared.pak", SyntheticPak.ForModule("PhotoMode", "PhotoMode", PhotoModeUuid, SyntheticPak.Version(1)));
        var catalog = CreateCatalog();

        var first = catalog.GetModules(DataFolder);
        catalog.PakReads.Should().Be(2);

        var second = catalog.GetModules(DataFolder);

        catalog.PakReads.Should().Be(2, "nothing changed on disk");
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void APakThatChangedIsReadAgain()
    {
        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(4)));
        var catalog = CreateCatalog();
        catalog.GetModules(DataFolder)![GustavUuid].SemanticVersion.Major.Should().Be(4);

        WritePak("Gustav.pak", SyntheticPak.ForModule("Gustav", "Gustav", GustavUuid, SyntheticPak.Version(5)));

        catalog.GetModules(DataFolder)![GustavUuid].SemanticVersion.Major.Should().Be(5);
        catalog.PakReads.Should().Be(2);
    }

    /// <summary>
    /// Opt-in: set BG3_GAME_FOLDER to a real install to check the modules the game ships.
    /// </summary>
    [Fact]
    public void TheRealGameShipsTheModulesModsDependOn()
    {
        var gameFolder = Environment.GetEnvironmentVariable("BG3_GAME_FOLDER");
        Assert.SkipWhen(string.IsNullOrEmpty(gameFolder), "BG3_GAME_FOLDER is not set");

        var data = FileSystem.Shared.FromUnsanitizedFullPath(gameFolder!).Combine("Data");

        var modules = CreateCatalog().GetModules(data);

        modules.Should().NotBeNull();
        modules!.Values.Select(m => m.Name).Should().Contain(["GustavX", "PhotoMode", "CrossplayUI"]);
    }
}
