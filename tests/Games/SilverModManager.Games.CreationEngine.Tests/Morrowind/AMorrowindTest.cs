using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.OpenMw;
using SilverModManager.Games.CreationEngine.Tests.OpenMw;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind;

/// <summary>
/// The Morrowind row over a stubbed install, with the OpenMW config roots in temp folders. The native root holds an
/// openmw.cfg when <see cref="OpenMwConfigText"/> is set.
/// </summary>
public abstract class AMorrowindTest<TTest>(ITestOutputHelper helper)
    : AIsolatedGameTest<TTest, CreationEngine.Morrowind.Morrowind>(helper)
    where TTest : class
{
    /// <summary>The text of the native root's openmw.cfg, or null for no config anywhere.</summary>
    protected virtual string? OpenMwConfigText => null;

    /// <summary>Plugins already in Data Files when the game is first managed, besides the masters.</summary>
    protected virtual string[] PluginsOnDisk => [];

    /// <summary>The text of a Morrowind.ini placed in the game folder before the game is managed, or null for none.</summary>
    protected virtual string? MorrowindIni => null;

    protected StubbedMorrowindInstall Install => ServiceProvider.GetRequiredService<StubbedMorrowindInstall>();

    protected TempOpenMwConfigRoots Roots => (TempOpenMwConfigRoots)ServiceProvider.GetRequiredService<IOpenMwConfigRoots>();

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddCreationEngine().AddTempOpenMwConfigRoots().AddStubbedMorrowind();

    protected override ValueTask PrepareGameInstall()
    {
        Install.Write(PluginsOnDisk, MorrowindIni);
        if (OpenMwConfigText is { } text) Roots.WriteConfig(0, text);
        return ValueTask.CompletedTask;
    }

    protected AbsolutePath Config => Roots.NativeConfig / "openmw.cfg";

    protected AbsolutePath Ini => Install.Folder / "Morrowind.ini";

    protected async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    protected async Task<LoadoutItemGroup.ReadOnly> InstallPlugin(Loadout.ReadOnly loadout, string fileName)
    {
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", StubbedMorrowindInstall.Plugin() } });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        return installed.LoadoutItemGroup!.Value;
    }
}
