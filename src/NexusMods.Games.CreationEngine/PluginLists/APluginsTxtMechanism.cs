using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder.Formats;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>
/// A game that reads its plugins from a text file with one plugin per line, in load order. The two flavours differ
/// only in how they mark an active plugin.
/// </summary>
public abstract class APluginsTxtMechanism : IPluginListMechanism
{
    /// <summary>The text of the plugin list for plugins in load order, early loaders included.</summary>
    public abstract string Render(IEnumerable<PluginListEntry> entries);

    /// <summary>The entries of a plugin list in file order.</summary>
    public abstract IReadOnlyList<PluginListEntry> Parse(string text);

    public bool OrdersByTimestamp => false;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        yield return new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game, this);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(game.PluginsFile);
        if (!file.FileExists) return null;

        var entries = Parse(await file.ReadAllTextAsync()).Select(e => (e.Name, e.Active)).ToList();
        return new GamePluginListState(entries);
    }
}

/// <summary>Active plugins are prefixed with <c>*</c> and unmarked ones are inactive (Skyrim SE, Fallout 4 and newer).</summary>
public sealed class AsteriskMechanism : APluginsTxtMechanism
{
    // The game loads its early loaders without being told, so the file leaves them out.
    public override string Render(IEnumerable<PluginListEntry> entries) => AsteriskPluginList.Render(entries, writeEarlyLoaders: false);

    public override IReadOnlyList<PluginListEntry> Parse(string text) => AsteriskPluginList.Parse(text);
}

/// <summary>One plugin name per line; everything listed is active (Skyrim LE, Enderal).</summary>
public sealed class PlainMechanism : APluginsTxtMechanism
{
    public override string Render(IEnumerable<PluginListEntry> entries) => PlainPluginList.Render(entries);

    public override IReadOnlyList<PluginListEntry> Parse(string text) => PlainPluginList.Parse(text);
}
