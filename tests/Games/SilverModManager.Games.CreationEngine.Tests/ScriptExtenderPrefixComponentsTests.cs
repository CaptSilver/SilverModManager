using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A script extender loader is a Windows program that needs the Visual C++ runtime in the prefix, so the
/// requirement follows the loader being in the loadout, not the game alone.
/// </summary>
public class ScriptExtenderPrefixComponentsTests(ITestOutputHelper helper) : AIsolatedGameTest<ScriptExtenderPrefixComponentsTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public async Task WithoutTheScriptExtenderNothingIsRequired()
    {
        var loadout = await CreateLoadout();

        WinePrefixRequirements.For(Game, loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task WithTheScriptExtenderInTheLoadoutTheVisualCppRuntimeIsRequired()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "SKSE");
            AddFile(tx, loadout, group, KnownPaths.SKSE64Loader);
            await tx.Commit();
        }
        Refresh(ref loadout);

        WinePrefixRequirements.For(Game, loadout).Should().Equal(PrefixComponents.VisualCppRuntime.Id);
    }

    [Fact]
    public void TheGameCheckingItsPrefixIsTheSharedEmitter()
    {
        Game.DiagnosticEmitters.OfType<SilverModManager.Games.Generic.Diagnostics.WinePrefixComponentsEmitter>().Should().ContainSingle();
    }
}
