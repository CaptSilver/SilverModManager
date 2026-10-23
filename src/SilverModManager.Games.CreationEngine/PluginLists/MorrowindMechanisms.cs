using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.PluginLists;

/// <summary>
/// Morrowind without OpenMW: the <c>[Game Files]</c> section of Morrowind.ini names the active plugins and the
/// game loads them in the order of the plugin files' times, like Oblivion. The times are set while deploying
/// (<see cref="ITimestampOrderedGame"/>).
/// </summary>
public sealed class MorrowindIniMechanism : IPluginListMechanism
{
    public bool OrdersByTimestamp => true;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        yield return new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game, new MorrowindIniFormat());
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var file = installation.Locations.ToAbsolutePath(game.PluginsFile);
        if (!file.FileExists) return null;

        var active = MorrowindIniGameFiles.Parse(await ReadIni(file)).ToHashSet(PluginNameComparer.Instance);
        return TimestampMechanism.OrderedByTime(game, installation, active);
    }

    internal static async Task<string> ReadIni(AbsolutePath file) => PluginListEncoding.DecodeWindows1252(await file.ReadAllBytesAsync());
}

/// <summary>
/// Morrowind run through OpenMW: openmw.cfg lists the plugins in the order OpenMW loads them, and a plugin is
/// off when it has no <c>content=</c> line. Morrowind.ini is written with the same order for anyone launching the
/// original game from the same folder, but only openmw.cfg is read back.
/// </summary>
public sealed class OpenMwMechanism : IPluginListMechanism
{
    // OpenMW does not look at times, but the ini beside it does and the same order has to hold for both.
    public bool OrdersByTimestamp => true;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILogger<PluginsFile>>();
        yield return new PluginsFile(logger, game, new OpenMwConfigFormat(), Morrowind.Morrowind.OpenMwConfigFile);
        // The ini is not the one that decides, so a difference there is not a notice of its own.
        yield return new PluginsFile(logger, game, new MorrowindIniFormat(), comparesWithGame: false);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(Morrowind.Morrowind.OpenMwConfigFile);
        if (!file.FileExists) return null;

        var names = OpenMwContentWriter.ContentNames(new OpenMwConfigFormat().Decode(await file.ReadAllBytesAsync()));
        return new GamePluginListState(names.Select(name => (name, true)).ToList());
    }
}
