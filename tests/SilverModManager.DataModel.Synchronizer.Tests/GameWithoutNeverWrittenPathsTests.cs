using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A game whose synchronizer does not claim any path as never written keeps its game folder fully managed: loadout
/// files are deployed into it and files that appear in it are ingested.
/// </summary>
public class GameWithoutNeverWrittenPathsTests(ITestOutputHelper helper) : ASteamInstalledGameTest<GameWithoutNeverWrittenPathsTests, Cyberpunk2077Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames();
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => CyberpunkSteamInstall.Install(library);

    private IReadOnlyList<string> OverridesPaths(Loadout.ReadOnly loadout)
    {
        return LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();
    }

    [Fact]
    public async Task ALoadoutFileInTheGameFolderIsDeployedThere()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"r6/scripts/mod.reds"], loadout.LoadoutId, "A mod");
            await tx.Commit();
        }

        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / "r6/scripts/mod.reds").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFileDroppedIntoTheGameFolderIsIngestedIntoTheOverrides()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var dropped = GameFolder / "r6/scripts/dropped.reds";
        dropped.Parent.CreateDirectory();
        await dropped.WriteAllTextAsync("// dropped in");

        await Synchronizer.Synchronize(Refresh(loadout));

        OverridesPaths(Refresh(loadout)).Should().Contain("r6/scripts/dropped.reds");
    }
}
