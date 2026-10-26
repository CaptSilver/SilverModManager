using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.PluginLists;

/// <summary>
/// Oblivion, Fallout 3 and New Vegas: plugins.txt names the active plugins and the load order is the modification
/// time of the plugin files in Data. The app writes the list; the times are set while deploying
/// (<see cref="ITimestampOrderedGame"/>).
/// </summary>
public sealed class TimestampMechanism : IPluginListMechanism
{
    private const string IniFileName = "Oblivion.ini";

    private readonly bool _locationFollowsIni;

    /// <param name="locationFollowsIni">
    /// Oblivion only: Plugins.txt moves from AppData to the game folder when Oblivion.ini sets
    /// <c>bUseMyGamesDirectory</c> to 0 (libloadorder game_settings.rs, oblivion_plugins_file_path).
    /// </param>
    public TimestampMechanism(bool locationFollowsIni = false) => _locationFollowsIni = locationFollowsIni;

    public bool OrdersByTimestamp => true;

    /// <summary>Where the game reads its active plugins from on this installation.</summary>
    public GamePath PluginsFilePath(ICreationEngineGame game, GameInstallation installation)
    {
        if (!_locationFollowsIni) return game.PluginsFile;

        // libloadorder reads only the game folder's ini and assumes My Games without one. Reading the My Games copy
        // then is our own fallback.
        var locations = installation.Locations;
        foreach (var location in new[] { LocationId.Game, LocationId.Preferences })
        {
            if (!locations.ContainsKey(location)) continue;

            var ini = locations.ToAbsolutePath(new GamePath(location, IniFileName));
            if (!ini.FileExists) continue;

            // The game writes its ini in Windows-1252; the key and its value are ASCII, which the document reads in any encoding.
            var usesMyGames = OblivionIni.UsesMyGamesDirectory(IniDocument.Parse(File.ReadAllBytes(ini.ToString())));
            return usesMyGames ? game.PluginsFile : new GamePath(LocationId.Game, game.PluginsFile.Path);
        }

        return game.PluginsFile;
    }

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var path = PluginsFilePath(game, loadout.InstallationInstance);
        yield return new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game, new PlainFormat(), path);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var locations = installation.Locations;
        var file = locations.ToAbsolutePath(PluginsFilePath(game, installation));
        if (!file.FileExists) return null;

        var active = PlainPluginList.Parse(PluginListEncoding.DecodeWindows1252(await file.ReadAllBytesAsync())).Select(e => e.Name).ToHashSet(PluginNameComparer.Instance);

        return OrderedByTime(game, installation, active);
    }

    /// <summary>
    /// Every plugin in the game's Data folder in the order the game loads them: by time, with the file name
    /// settling a tie (libloadorder timestamp_based.rs). <paramref name="active"/> is what the game's list switches on.
    /// </summary>
    internal static GamePluginListState OrderedByTime(ICreationEngineGame game, GameInstallation installation, ISet<string> active)
    {
        var data = installation.Locations.ToAbsolutePath(game.DataPath);
        var dataFiles = data.DirectoryExists() ? data.EnumerateFiles().ToList() : [];
        active.UnionWith(game.ImplicitlyActivePlugins(dataFiles.Select(f => f.FileName)));

        var entries = dataFiles
            .Where(f => game.PluginExtensions.Contains(f.Extension))
            .Select(f => (Name: f.FileName, Mtime: f.FileInfo.LastWriteTimeUtc))
            .OrderBy(f => f.Mtime)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (f.Name, active.Contains(f.Name)))
            .ToList();
        return new GamePluginListState(entries);
    }
}
