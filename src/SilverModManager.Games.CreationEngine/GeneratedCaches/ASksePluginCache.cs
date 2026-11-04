using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Games.CreationEngine.GeneratedCaches;

/// <summary>
/// A cache kept by a script extender plugin that sits in <c>Data/SKSE/Plugins</c> beside its ini. The plugin is
/// deployed when its DLL is, and its ini may move the cache to a folder of the player's choosing.
/// </summary>
public abstract class ASksePluginCache(IReadOnlyCollection<GameId> games, ILogger logger) : IGeneratedCache
{
    /// <summary>Where script extender plugins and their ini files go.</summary>
    protected static readonly GamePath PluginsFolder = new(LocationId.Game, "Data/SKSE/Plugins");

    /// <summary>The game's data folder, where plugins and archives sit.</summary>
    protected static readonly GamePath DataFolder = new(LocationId.Game, "Data");

    protected ILogger Logger { get; } = logger;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>The plugin's file name without the extension; its ini carries the same name.</summary>
    protected abstract string PluginName { get; }

    /// <summary>The ini key that, when set, names the folder the cache lives in.</summary>
    protected abstract string FolderKey { get; }

    /// <summary>The folder the cache uses when the ini does not name one, or null when there is none on this installation.</summary>
    protected abstract AbsolutePath? DefaultFolder(GameInstallation installation);

    /// <inheritdoc />
    public abstract bool IsOwnedPath(GamePath path);

    /// <inheritdoc />
    public abstract IReadOnlyList<AbsolutePath> StaleItems(GameInstallation installation, IReadOnlyCollection<GamePath> changed);

    private GamePath DllPath => PluginsFolder / $"{PluginName}.dll";
    private GamePath IniPath => PluginsFolder / $"{PluginName}.ini";

    /// <inheritdoc />
    public bool AppliesTo(GameInstallation installation, IReadOnlyCollection<GamePath> changed)
    {
        if (!games.Contains(installation.Game.GameId)) return false;

        // The DLL in the changed paths is one this Apply removed; the cache folder is what an earlier Apply left.
        return installation.Locations.ToAbsolutePath(DllPath).FileExists
            || changed.Contains(DllPath)
            || OwnedFolders(installation).Any(folder => folder.DirectoryExists());
    }

    /// <inheritdoc />
    public IReadOnlyList<AbsolutePath> OwnedFolders(GameInstallation installation)
    {
        var folder = IniFolder(installation) ?? DefaultFolder(installation);
        return folder is { } owned ? [owned] : [];
    }

    /// <summary>
    /// Whether it is safe to empty the folder. An ini can name any folder, so a folder that is, holds or sits inside
    /// any of the game's locations is refused, and so is a top-level one. Both sides are compared after following
    /// links: <c>Z:\var\home\me</c> and a symlink onto the game folder name the same place as the locations do,
    /// though not as strings.
    /// </summary>
    protected bool IsSafeToEmpty(GameInstallation installation, AbsolutePath folder)
    {
        var real = HostPaths.RealPath(folder.ToString()).TrimEnd('/');
        if (real.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;
        foreach (var (_, location) in installation.Locations)
        {
            var other = HostPaths.RealPath(location.Path.ToString()).TrimEnd('/');
            if (real == other || real.StartsWith(other + "/", StringComparison.Ordinal) || other.StartsWith(real + "/", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private AbsolutePath? IniFolder(GameInstallation installation)
    {
        if (ReadIniValue(installation, FolderKey) is not { } value) return null;

        // A relative value would be taken against a folder the plugin does not document, so only a full path is honoured.
        var rooted = value.StartsWith('/') || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
        if (!rooted)
        {
            Logger.LogWarning("{Name} names a relative cache folder '{Value}' in its ini, which is not followed; using the default folder", Name, value);
            return null;
        }

        var anchor = PrefixDriveC(installation) ?? installation.Locations.ToAbsolutePath(DllPath).Parent.ToString();
        var host = HostPaths.FromWindows(value, anchor);
        if (host is null)
        {
            Logger.LogWarning("{Name} names the cache folder '{Value}' in its ini, which does not map to a folder on this machine", Name, value);
            return null;
        }

        return installation.Locations.ToAbsolutePath(DllPath).FileSystem.FromUnsanitizedFullPath(host);
    }

    /// <summary>
    /// Whether the folder sits inside the game's own Wine prefix, compared after following links. An ini is a file a mod
    /// archive brings, so a folder it names is only cleared when it is in the prefix the game already owns.
    /// </summary>
    protected bool IsInsidePrefix(GameInstallation installation, AbsolutePath folder)
    {
        string driveC;
        if (installation.LocatorResult.WinePrefixPath is { } winePrefix) driveC = (winePrefix / "drive_c").ToString();
        else if (installation.Locations.TryGetValue(LocationId.AppData, out var appData)
                 && appData.Path.ToString() is var appDataPath && appDataPath.IndexOf("/drive_c/", StringComparison.Ordinal) is var at and >= 0)
            driveC = appDataPath[..(at + "/drive_c".Length)];
        else return false;

        var real = HostPaths.RealPath(folder.ToString()).TrimEnd('/');
        var prefix = HostPaths.RealPath(driveC).TrimEnd('/');
        return real.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private string? PrefixDriveC(GameInstallation installation)
    {
        if (installation.LocatorResult.WinePrefixPath is { } prefix) return (prefix / "drive_c").ToString();
        if (installation.Locations.TryGetValue(LocationId.AppData, out var appData) && appData.Path.ToString().Contains("/drive_c/", StringComparison.Ordinal))
            return appData.Path.ToString();
        return null;
    }

    private string? ReadIniValue(GameInstallation installation, string key)
    {
        var ini = installation.Locations.ToAbsolutePath(IniPath);
        if (!ini.FileExists) return null;

        try
        {
            var document = IniDocument.Parse(File.ReadAllBytes(ini.ToString()));
            foreach (var section in document.Sections.Prepend(""))
            {
                if (document.Get(section, key)?.Trim() is { Length: > 0 } value) return value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(e, "Could not read {Ini}; the default cache folder is used", ini);
        }

        return null;
    }
}
