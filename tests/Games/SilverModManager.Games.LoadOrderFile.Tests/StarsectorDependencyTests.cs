using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>Starsector refuses to enable a mod whose dependency is missing, so the report is critical.</summary>
public class StarsectorDependencyTests(ITestOutputHelper outputHelper)
    : ALoadOrderFileGameTests<StarsectorDependencyTests>(outputHelper, LoadOrderFileGames.Starsector)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddDiagnosticWriter();

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(Loadout.ReadOnly loadout, string id, params string[] dependencies)
    {
        var deps = string.Join(",", dependencies.Select(dependency => $"{{\"id\":\"{dependency}\"}}"));
        var archive = await AddArchiveWithContents($"{id}.zip", ($"{id}/mod_info.json", $"{{\"id\":\"{id}\",\"name\":\"{id}\",\"dependencies\":[{deps}]}}"), ($"{id}/data/x.csv", ""));
        return (await LoadoutManager.InstallItems([archive.AsLibraryFile().AsLibraryItem()], loadout.LoadoutId)).Single();
    }

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var current = Refresh(loadout);
        var syncTree = (await Synchronizer.BuildSyncTree(current)).ToFrozenDictionary();
        ILoadoutDiagnosticEmitter emitter = Game.DiagnosticEmitters.OfType<ManifestDependencyEmitter>().Single();
        return await emitter.Diagnose(current, syncTree, CancellationToken.None).ToArrayAsync();
    }

    [Fact]
    public async Task AStarsectorModWithAMissingDependencyIsCritical()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "my_mod", "lw_lazylib");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.MissingDependencyMessageData>>().Which.MessageData.Missing.Should().Be("lw_lazylib");
    }

    [Fact]
    public async Task AnInstalledDependencyIsSatisfied()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "my_mod", "lw_lazylib");
        await InstallMod(loadout, "lw_lazylib");

        (await Diagnose(loadout)).Should().BeEmpty();
    }
}
