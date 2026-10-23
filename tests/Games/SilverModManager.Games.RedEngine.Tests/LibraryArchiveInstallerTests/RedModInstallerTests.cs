using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Models;
using SilverModManager.Games.RedEngine.ModInstallers;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.RedEngine.Tests.LibraryArchiveInstallerTests;

public class RedModInstallerTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<PathBasedInstallerTests, Cyberpunk2077Game>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"));
    }
    
    [Theory]
    [InlineData("several_red_mods.7z")]
    [InlineData("one_mod.7z")]
    public async Task CanInstallRedMod(string filename)
    {
        var fullPath = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("LibraryArchiveInstallerTests/Resources/" + filename);

        var loadout = await CreateLoadout();
        var libraryArchive = await RegisterLocalArchive(fullPath);
        var group = await Install(typeof(RedModInstaller), loadout, libraryArchive);
        
        await VerifyGroup(libraryArchive, group).UseParameters(filename);
    }
}
