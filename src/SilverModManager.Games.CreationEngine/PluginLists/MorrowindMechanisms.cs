using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.OpenMw;
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
/// OpenMW: openmw.cfg lists the plugins in the order OpenMW loads them, and a plugin is off when it has no
/// <c>content=</c> line. The file is the only intrinsic one; OpenMW never reads Morrowind.ini.
/// </summary>
public sealed class OpenMwMechanism : IPluginListMechanism
{
    // OpenMW loads in the order of the content= lines, whatever the plugin files' times are.
    public bool OrdersByTimestamp => false;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        yield return new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game, new OpenMwConfigFormat(), OpenMwConstants.OpenMwConfigFile);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(OpenMwConstants.OpenMwConfigFile);
        if (!file.FileExists) return null;

        var text = new OpenMwConfigFormat().Decode(await file.ReadAllBytesAsync());
        return new GamePluginListState(OpenMwContentWriter.ContentNames(text).Select(name => (name, true)).ToList())
        {
            Groundcover = OpenMwContentWriter.GroundcoverNames(text).ToHashSet(PluginNameComparer.Instance),
        };
    }
}
