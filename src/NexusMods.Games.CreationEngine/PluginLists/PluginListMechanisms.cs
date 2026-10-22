using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Formats;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>Active plugins are prefixed with <c>*</c> and unmarked ones are inactive (Skyrim SE, Fallout 4 and newer).</summary>
public sealed class AsteriskMechanism : IPluginListMechanism, IPluginListFormat
{
    // The game loads its early loaders without being told, so the file leaves them out.
    public string Render(IEnumerable<PluginListEntry> entries) => AsteriskPluginList.Render(entries, writeEarlyLoaders: false);

    public IReadOnlyList<PluginListEntry> Parse(string text) => AsteriskPluginList.Parse(text);

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

/// <summary>
/// Skyrim LE, Enderal and Oblivion Remastered: plugins.txt names the active plugins and loadorder.txt, in the same
/// folder, names every plugin in load order.
/// </summary>
public sealed class TextfileMechanism : IPluginListMechanism
{
    public const string LoadOrderFileName = "loadorder.txt";

    public bool OrdersByTimestamp => false;

    public static GamePath LoadOrderPath(ICreationEngineGame game) => game.PluginsFile.Parent / LoadOrderFileName;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILogger<PluginsFile>>();
        yield return new PluginsFile(logger, game, new PlainFormat());
        // The replaced-edits warning compares plugins.txt: a loadorder.txt entry carries no switch to compare.
        yield return new PluginsFile(logger, game, new LoadOrderFormat(), LoadOrderPath(game), comparesWithGame: false);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var locations = loadout.InstallationInstance.Locations;
        var activeFile = locations.ToAbsolutePath(game.PluginsFile);
        var orderFile = locations.ToAbsolutePath(LoadOrderPath(game));
        if (!activeFile.FileExists && !orderFile.FileExists) return null;

        var active = activeFile.FileExists
            ? PlainPluginList.Parse(await activeFile.ReadAllTextAsync()).Select(e => e.Name).ToList()
            : [];
        // With no loadorder.txt the game's own order is plugins.txt's.
        if (!orderFile.FileExists) return new GamePluginListState(active.Select(name => (name, true)).ToList());

        var activeSet = active.ToHashSet(PluginNameComparer.Instance);
        var order = LoadOrderTxt.Parse(await orderFile.ReadAllTextAsync());
        var listed = order.ToHashSet(PluginNameComparer.Instance);

        // An active plugin loadorder.txt does not know is still loaded; libloadorder appends it after the listed ones.
        var entries = order.Select(name => (name, activeSet.Contains(name)))
            .Concat(active.Where(name => !listed.Contains(name)).Select(name => (name, true)))
            .ToList();
        return new GamePluginListState(entries);
    }
}
