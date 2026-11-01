using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests;

/// <summary>The shared checks; each real game runs them with its own loader package.</summary>
public abstract class ABepInExMissingEmitterTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : ALoadoutDiagnosticEmitterTest<TTest, IGame, BepInExMissingEmitter>(outputHelper, definition.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddUnityGames()
            .AddUniversalGameLocator(definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private async Task<EntityId> AddFiles(Sdk.Loadouts.LoadoutId loadout, string group, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var groupId = AddEmptyGroup(tx, loadout, group);
        foreach (var path in paths) AddFile(tx, loadout, groupId, new GamePath(LocationId.Game, path));
        return (await tx.Commit())[groupId.Value];
    }

    [Fact]
    public async Task APluginWithoutThePackIsReportedWithTheLoaderPackage()
    {
        var loadout = await CreateLoadout();
        var plugin = await AddFiles(loadout, "Zehs-REPOLib", "BepInEx/plugins/Zehs-REPOLib/REPOLib.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.Unity", number: 1));
        var details = diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        details.Should().Contain($"`{definition.ThunderstoreLoaderPackage}`");
        details.Should().Contain("Thunderstore");
        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.BepInExMissingMessageData>>()
            .Which.MessageData.Group.DataId.Value.Should().Be(plugin);
    }

    [Fact]
    public async Task NothingIsReportedOnceThePackIsInstalled()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "pack", "winhttp.dll", "doorstop_config.ini");
        await AddFiles(loadout, "plugin", "BepInEx/plugins/Zehs-REPOLib/REPOLib.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task NothingIsReportedWithoutPlugins()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "config", "BepInEx/config/a.cfg");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

public class RepoBepInExMissingEmitterTests(ITestOutputHelper outputHelper)
    : ABepInExMissingEmitterTests<RepoBepInExMissingEmitterTests>(outputHelper, UnityGames.Repo);

public class PeakBepInExMissingEmitterTests(ITestOutputHelper outputHelper)
    : ABepInExMissingEmitterTests<PeakBepInExMissingEmitterTests>(outputHelper, UnityGames.Peak);
