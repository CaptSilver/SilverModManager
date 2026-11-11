using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Unreal.Tests.Fixtures;

namespace SilverModManager.Games.Unreal.Tests;

/// <summary>
/// Clair Obscur: Expedition 33 on top of the shared row tests. The game ships IoStore paks (.pak with .utoc and .ucas)
/// and is commonly patched with an Ultimate ASI Loader (dsound.dll) plus .asi plugins beside the shipping binary; the
/// module has no rule for a bare ASI, so only an archive that packs the game's own Binaries folder installs one.
/// </summary>
public class ClairObscurTests(ITestOutputHelper outputHelper) : AUnrealRowTests<ClairObscurTests>(outputHelper, UnrealGames.ClairObscurExpedition33)
{
    private ALibraryArchiveInstaller Installer => Game.LibraryItemInstallers.OfType<UnrealModInstaller>().Single();

    [Fact]
    public void ThePaksAndBinariesFoldersResolveBelowSandfall()
    {
        var layout = UnrealGames.ClairObscurExpedition33.Layout;

        layout.TryRoute("Mod_P.pak", out var pak).Should().BeTrue();
        layout.TryRoute("UE4SS.dll", out var loader).Should().BeTrue();

        pak.Should().Be(new SilverModManager.Sdk.Games.GamePath(SilverModManager.Sdk.Games.LocationId.Game, "Sandfall/Content/Paks/~mods/Mod_P.pak"));
        loader.Should().Be(new SilverModManager.Sdk.Games.GamePath(SilverModManager.Sdk.Games.LocationId.Game, "Sandfall/Binaries/Win64/UE4SS.dll"));
    }

    [Fact]
    public async Task AnAsiPackedUnderTheGamesBinariesFolderInstallsBesideTheShippingBinary()
    {
        var archive = await AddFromPaths("Sandfall/Binaries/Win64/ClairObscurFix.asi", "Sandfall/Binaries/Win64/ClairObscurFix.ini");

        var (result, targets) = await TryInstall(Installer, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            OnGame("Sandfall/Binaries/Win64/ClairObscurFix.asi"),
            OnGame("Sandfall/Binaries/Win64/ClairObscurFix.ini"));
    }

    [Fact]
    public async Task ABareAsiWithItsLoaderIsNotSupportedBecauseTheModuleHasNoRuleForIt()
    {
        var archive = await AddFromPaths("dsound.dll", "ClairObscurFix.asi");

        var (result, _) = await TryInstall(Installer, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }
}
