using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>Every file row of the loadout, enabled or disabled: what a log's fingerprint and a dropped verdict are compared against.</summary>
public sealed class LoadoutFileIndex
{
    private LoadoutFileIndex(IReadOnlySet<GamePath> paths, IReadOnlySet<GamePath> disabledPaths, IReadOnlySet<string> pluginNames, IReadOnlySet<string> skseDllNames)
    {
        Paths = paths;
        DisabledPaths = disabledPaths;
        PluginNames = pluginNames;
        SkseDllNames = skseDllNames;
    }

    public IReadOnlySet<GamePath> Paths { get; }

    /// <summary>Rows whose item is disabled, itself or through a parent: a disabled mod's files. An Overrides row is never in here.</summary>
    public IReadOnlySet<GamePath> DisabledPaths { get; }

    /// <summary>The plugins directly under the game's Data folder.</summary>
    public IReadOnlySet<string> PluginNames { get; }

    /// <summary>The DLLs directly under the script extender's plugin folder.</summary>
    public IReadOnlySet<string> SkseDllNames { get; }

    /// <summary>An index holding only plugin and script extender DLL names, for comparing a log's fingerprint without a loadout.</summary>
    internal static LoadoutFileIndex FromNames(IEnumerable<string> plugins, IEnumerable<string> dlls) =>
        new(new HashSet<GamePath>(), new HashSet<GamePath>(), new HashSet<string>(plugins, PluginNameComparer.Instance), new HashSet<string>(dlls, PluginNameComparer.Instance));

    public static LoadoutFileIndex For(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var paths = new HashSet<GamePath>();
        var disabled = new HashSet<GamePath>();
        var plugins = new HashSet<string>(PluginNameComparer.Instance);
        var dlls = new HashSet<string>(PluginNameComparer.Instance);

        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var path = (GamePath)item.TargetPath;
            paths.Add(path);
            if (!item.AsLoadoutItem().IsEnabled()) disabled.Add(path);

            if (path.LocationId != LocationId.Game) continue;
            if (path.Parent == game.DataPath && game.PluginExtensions.Contains(path.Extension)) plugins.Add(path.FileName.ToString());
            else if (game.ScriptExtenderPluginFolder is { } folder && path.Parent == folder && CrashLogPaths.IsDll(path)) dlls.Add(path.FileName.ToString());
        }

        return new LoadoutFileIndex(paths, disabled, plugins, dlls);
    }
}
