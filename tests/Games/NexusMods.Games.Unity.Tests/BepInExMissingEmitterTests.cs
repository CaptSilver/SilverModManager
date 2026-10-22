using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.TestFramework;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Games.Unity.Tests.Fixtures;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Unity.Tests;

/// <summary>The shared checks; each real game runs them with its own loader package.</summary>
public abstract class ABepInExMissingEmitterTests<TTest, TGame>(ITestOutputHelper outputHelper, string expectedPackage)
    : ALoadoutDiagnosticEmitterTest<TTest, TGame, BepInExMissingEmitter>(outputHelper)
    where TGame : AUnityGame<TGame>, IGameData<TGame>
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddUnityGames()
            .AddUniversalGameLocator<TGame>(new Version("1.0"));
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

        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.Unity", number: 1));
        var details = diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        details.Should().Contain($"`{expectedPackage}`");
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
    : ABepInExMissingEmitterTests<RepoBepInExMissingEmitterTests, Unity.Repo.Repo>(outputHelper, "BepInEx-BepInExPack");

public class PeakBepInExMissingEmitterTests(ITestOutputHelper outputHelper)
    : ABepInExMissingEmitterTests<PeakBepInExMissingEmitterTests, Unity.Peak.Peak>(outputHelper, "BepInEx-BepInExPack_PEAK");
