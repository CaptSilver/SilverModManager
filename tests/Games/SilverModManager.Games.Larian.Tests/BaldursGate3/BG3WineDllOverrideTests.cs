using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.BaldursGate3.Installers;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class BG3WineDllOverrideTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<BG3WineDllOverrideTests, Larian.BaldursGate3.BaldursGate3>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddBaldursGate3()
            .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.6.1"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public async Task BaldursGate3RequiresDWriteOnlyWhenTheScriptExtenderIsInTheLoadout()
    {
        var requirements = Game.Should().BeAssignableTo<IWineDllOverrideRequirements>().Subject;
        var loadout = await CreateLoadout();

        requirements.RequiredWineDllOverrides(loadout).Should().BeEmpty();

        var archive = await AddFromPaths("DWrite.dll", "ScriptExtenderSettings.json");
        await Install(typeof(BG3SEInstaller), loadout, archive);
        Refresh(ref loadout);

        requirements.RequiredWineDllOverrides(loadout).Select(o => o.ToString()).Should().Equal("DWrite=n,b");
    }
}
