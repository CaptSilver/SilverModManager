using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.OpenMw;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// The OpenMW row over a stubbed Morrowind install with the three official masters in Data Files, two config roots
/// in temp folders and a fake <c>openmw</c> program. The Morrowind row is registered over the same folder, so a test
/// can put both rows on one install.
/// </summary>
public abstract class AOpenMwGameTest<TTest>(ITestOutputHelper helper)
    : AIsolatedGameTest<TTest, CreationEngine.OpenMw.OpenMw>(helper)
    where TTest : class
{
    /// <summary>Indexes (0 for native, 1 for Flatpak) of the roots that hold an openmw.cfg.</summary>
    protected virtual int[] RootsWithConfig => [0];

    /// <summary>What the roots that hold an openmw.cfg have in it.</summary>
    protected virtual string ConfigText => Install.Config();

    /// <summary>Plugins already in the Morrowind Data Files when the game is first managed, besides the masters.</summary>
    protected virtual string[] PluginsInMorrowind => [];

    /// <summary>Plugins already in OpenMW's data-local folder when the game is first managed.</summary>
    protected virtual string[] PluginsInDataLocal => [];

    protected StubbedMorrowindInstall Install => ServiceProvider.GetRequiredService<StubbedMorrowindInstall>();

    protected TempOpenMwConfigRoots Roots => (TempOpenMwConfigRoots)ServiceProvider.GetRequiredService<IOpenMwConfigRoots>();

    protected AbsolutePath Config => Roots.NativeConfig / "openmw.cfg";

    protected AbsolutePath DataLocal => Roots.Roots[0].UserDataFolder / "data";

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddCreationEngine().AddTempOpenMwConfigRoots().AddStubbedMorrowind();

    protected override ValueTask PrepareGameInstall()
    {
        Install.Write(PluginsInMorrowind);
        foreach (var root in RootsWithConfig) Roots.WriteConfig(root, ConfigText);
        if (PluginsInDataLocal.Length > 0)
        {
            DataLocal.CreateDirectory();
            foreach (var name in PluginsInDataLocal)
                File.WriteAllBytes((DataLocal / name).GetFullPath(), StubbedMorrowindInstall.Plugin());
        }
        return ValueTask.CompletedTask;
    }

    protected async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    protected async Task<LoadoutItemGroup.ReadOnly> InstallPlugin(Loadout.ReadOnly loadout, string fileName, params string[] masters)
    {
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", StubbedMorrowindInstall.Plugin(false, masters) } });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        return installed.LoadoutItemGroup!.Value;
    }

    protected async Task<string[]> ContentLines() =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("content=")).ToArray();
}
