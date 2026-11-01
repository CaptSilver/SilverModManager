using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.REEngine.Tests;

/// <summary>The shared checks; each real game runs them with its own release name.</summary>
public abstract class AReFrameworkMissingEmitterTests<TTest>(ITestOutputHelper outputHelper, ReEngineGameDefinition definition)
    : ALoadoutDiagnosticEmitterTest<TTest, IGame, ReFrameworkMissingEmitter>(outputHelper, definition.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddReEngineGames()
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
    public async Task AScriptWithoutTheLoaderIsReportedWithTheReleaseToDownload()
    {
        var loadout = await CreateLoadout();
        var script = await AddFiles(loadout, "A Script", "reframework/autorun/a.lua");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.REEngine", number: 1));
        var details = diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        details.Should().Contain($"`{definition.ReFrameworkRelease}`");
        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.ReFrameworkMissingMessageData>>()
            .Which.MessageData.Group.DataId.Value.Should().Be(script);
    }

    [Fact]
    public async Task NothingIsReportedOnceTheLoaderIsInstalled()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "loader", "dinput8.dll");
        await AddFiles(loadout, "script", "reframework/plugins/a.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task LooseGameFilesNeedNoLoader()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "textures", "natives/STM/a.tex.2");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

public class ResidentEvil2ReFrameworkMissingEmitterTests(ITestOutputHelper outputHelper)
    : AReFrameworkMissingEmitterTests<ResidentEvil2ReFrameworkMissingEmitterTests>(outputHelper, ReEngineGames.ResidentEvil2);

public class MonsterHunterWildsReFrameworkMissingEmitterTests(ITestOutputHelper outputHelper)
    : AReFrameworkMissingEmitterTests<MonsterHunterWildsReFrameworkMissingEmitterTests>(outputHelper, ReEngineGames.MonsterHunterWilds);
