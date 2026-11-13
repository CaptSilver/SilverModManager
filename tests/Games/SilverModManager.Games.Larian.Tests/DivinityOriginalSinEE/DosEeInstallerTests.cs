using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Games.Larian.DivinityOriginalSinEE;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSinEE;

public class DosEeInstallerTests(ITestOutputHelper outputHelper)
    : ALibraryArchiveInstallerTests<DosEeInstallerTests, Larian.DivinityOriginalSinEE.DivinityOriginalSinEE>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddDivinityOriginalSinEE()
        .AddUniversalGameLocator<Larian.DivinityOriginalSinEE.DivinityOriginalSinEE>(new Version("3.6.54"), locationsFileSystem: IsolatedKnownFolders.Create, platform: OSPlatform.Windows);

    [Fact]
    public async Task ADocsModFolderRoutesToMods()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("DOCS_MOD/x.pak");

        var group = await Install(typeof(GenericPatternMatchInstaller), loadout, archive);

        ChildrenFilesAndHashes(group).Select(c => c.GamePath).Should().Equal(new GamePath(LarianConstants.ModsLocationId, "x.pak"));
    }
}
