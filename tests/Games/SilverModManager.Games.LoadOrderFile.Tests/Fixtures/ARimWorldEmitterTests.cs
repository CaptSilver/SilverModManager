using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

/// <summary>
/// RimWorld with a loadout whose mods are installed through the game's installers and a Steam Workshop folder beside
/// the game, for the emitters that read the mods' manifests.
/// </summary>
public abstract class ARimWorldEmitterTests<TTest, TEmitter>(ITestOutputHelper outputHelper)
    : ALoadOrderFileGameTests<TTest>(outputHelper, LoadOrderFileGames.RimWorld)
    where TEmitter : ILoadoutDiagnosticEmitter
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddDiagnosticWriter();

    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { [Definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create,
            platform: OSPlatform.Windows,
            folderName: "steamapps/common/RimWorld");

    protected TEmitter Emitter => Game.DiagnosticEmitters.OfType<TEmitter>().Single();

    protected AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    protected static string About(string packageId, string rules = "")
        => $"<ModMetaData><name>{packageId}</name><packageId>{packageId}</packageId>{rules}</ModMetaData>";

    protected static string Rule(string element, params string[] ids) => $"<{element}>{string.Concat(ids.Select(id => $"<li>{id}</li>"))}</{element}>";

    /// <summary>Installs a mod whose folder, and listed name, is <paramref name="packageId"/> in lower case.</summary>
    protected async Task<LoadoutItemGroup.ReadOnly> InstallMod(Loadout.ReadOnly loadout, string packageId, string rules = "")
    {
        var folder = packageId.ToLowerInvariant();
        var archive = await AddArchiveWithContents($"{folder}.zip", ($"{folder}/About/About.xml", About(packageId, rules)), ($"{folder}/Defs/x.xml", ""));
        return (await LoadoutManager.InstallItems([archive.AsLibraryFile().AsLibraryItem()], loadout.LoadoutId)).Single();
    }

    protected async Task SubscribeToWorkshopMod(string itemId, string packageId, string rules = "")
    {
        var file = GameFolder.Parent.Parent.Combine($"workshop/content/294100/{itemId}/About/About.xml");
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(About(packageId, rules));
    }

    /// <summary>Puts the loadout's groups in this order, the winner first.</summary>
    protected async Task Prioritise(Loadout.ReadOnly loadout, params LoadoutItemGroup.ReadOnly[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        LoadoutManager.SetGroupOrder(loadout.LoadoutId, winnerToLoser.Select(group => group.LoadoutItemGroupId).ToArray(), tx);
        await tx.Commit();
    }

    protected async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var current = Refresh(loadout);
        var syncTree = (await Synchronizer.BuildSyncTree(current)).ToFrozenDictionary();
        return await Emitter.Diagnose(current, syncTree, CancellationToken.None).ToArrayAsync();
    }
}
