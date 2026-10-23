using FluentAssertions;
using SilverModManager.Games.FromSoft.Me3;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3PackageLayoutTests
{
    private static RelativePath[] Files(params string[] paths) => paths.Select(p => (RelativePath)p).ToArray();

    private static string? Root(params string[] paths)
        => Me3PackageLayout.TryFindRoot(Files(paths), out var root) ? root.Path : null;

    [Fact]
    public void ABareTreeIsItsOwnRoot() => Root("parts/wp_a.dcx", "menu/hud.dds").Should().Be("");

    [Fact]
    public void ASingleWrapperIsDescended() => Root("My Cool Mod/parts/wp_a.dcx").Should().Be("My Cool Mod");

    [Fact]
    public void WrappersAreDescendedRepeatedly() => Root("a/b/chr/c0000.dcx").Should().Be("a/b");

    [Fact]
    public void ModEngineLauncherFilesDoNotBlockDescent() => Root("modengine2_launcher.exe", "config_eldenring.toml", "launch.bat", "mod/parts/wp.dcx").Should().Be("mod");

    [Fact]
    public void AStrayFileOfAnotherKindBlocksDescent() => Root("somefile.dat", "ModFolder/parts/wp.dcx").Should().BeNull();

    [Fact]
    public void TwoWrappersAreNotDescended() => Root("a/parts/x.dcx", "b/parts/y.dcx").Should().BeNull();

    [Fact]
    public void NothingRecognisableIsNoRoot() => Root("random/thing.bin", "notes.docx").Should().BeNull();

    [Fact]
    public void AssetFolderNamesAreMatchedWithoutRegardToCase() => Root("Parts/wp_a.dcx").Should().Be("");

    [Fact]
    public void TheRegulationFileIsMatchedWithoutRegardToCase() => Root("Regulation.BIN").Should().Be("");

    [Fact]
    public void OptionFoldersAreTheTopLevelFoldersThatAreEachAPackage()
    {
        Me3PackageLayout.ListOptionDirs(Files("OPTION 1/menu/x.gfx", "OPTION 2/menu/y.gfx")).Should().Equal("OPTION 1", "OPTION 2");
    }

    [Fact]
    public void ANormalSingleRootTreeHasNoOptionFolders()
    {
        Me3PackageLayout.ListOptionDirs(Files("parts/x.dcx")).Should().BeEmpty();
    }

    [Fact]
    public void AFolderThatIsNotAPackageIsNotAnOption()
    {
        Me3PackageLayout.ListOptionDirs(Files("OPTION 1/menu/x.gfx", "docs/notes.docx")).Should().Equal("OPTION 1");
    }
}
