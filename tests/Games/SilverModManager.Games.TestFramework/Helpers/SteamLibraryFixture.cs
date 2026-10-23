using System.Text;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Networking.Steam.Tests;
using NexusMods.Paths;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>One file of a depot: the path as it sits in the game folder and its bytes.</summary>
public sealed record SteamFile(string Path, byte[] Content)
{
    public SteamFile(string path, string text) : this(path, Encoding.UTF8.GetBytes(text)) { }
}

/// <summary>Where the Steam client saved a depot's manifest, which decides which lookup finds it.</summary>
public enum ManifestCache
{
    /// <summary>In the client root's depotcache folder.</summary>
    ClientRoot,

    /// <summary>In the library's own steamapps/depotcache folder.</summary>
    Library,

    /// <summary>Not saved anywhere, like a depot Steam never downloaded a manifest for.</summary>
    None,
}

/// <summary>A depot of an installed game and the manifest the install is at.</summary>
public sealed record SteamDepot(uint DepotId, ulong ManifestId, IReadOnlyList<SteamFile> Files, ManifestCache Cache = ManifestCache.ClientRoot);

/// <summary>An installed game in a <see cref="SteamLibraryFixture"/>.</summary>
public sealed class SteamInstall
{
    internal SteamInstall(uint appId, string installDir, AbsolutePath folder, AbsolutePath appManifest)
    {
        AppId = appId;
        InstallDir = installDir;
        Folder = folder;
        AppManifest = appManifest;
    }

    public uint AppId { get; }
    public string InstallDir { get; }

    /// <summary>The game folder, spelled through the library's symlinked ancestor.</summary>
    public AbsolutePath Folder { get; }

    public AbsolutePath AppManifest { get; }
}

/// <summary>
/// A Steam client and a game library on disk, laid out the way the dev machine has them, made of synthetic
/// files only. The client root sits in a fake home directory. The library is on a second "mount" that is
/// reached through a symlinked ancestor: <c>mnt</c> is a link to <c>var-mnt</c>, as <c>/mnt</c> is to <c>/var/mnt</c>.
/// <para>
/// <see cref="FileSystem"/> maps every known folder into the fixture, so a locator built on it cannot see the
/// real home directory, Steam client or XDG folders of whoever runs the tests.
/// </para>
/// </summary>
public sealed class SteamLibraryFixture : IDisposable
{
    private const string LibraryContentId = "100";

    private readonly AbsolutePath _root;
    private readonly List<SteamInstall> _installs = [];

    public SteamLibraryFixture()
    {
        _root = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"steam-library-fixture-{Guid.NewGuid():N}";
        Home = _root / "home";

        SteamRoot = Home / ".steam" / "steam";
        var libraryTarget = _root / "var-mnt" / "games" / "SteamLibrary";
        libraryTarget.CreateDirectory();
        Directory.CreateSymbolicLink((_root / "mnt").ToString(), (_root / "var-mnt").ToString());
        Library = _root / "mnt" / "games" / "SteamLibrary";

        FileSystem = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath>
            {
                [KnownPath.HomeDirectory] = Home,
                [KnownPath.XDG_DATA_HOME] = Home / ".local" / "share",
                [KnownPath.XDG_CONFIG_HOME] = Home / ".config",
                [KnownPath.XDG_CACHE_HOME] = Home / ".cache",
                [KnownPath.XDG_STATE_HOME] = Home / ".local" / "state",
                [KnownPath.XDG_RUNTIME_DIR] = _root / "run",
                // The Windows-style folders games ask for resolve from the real user's account unless mapped too.
                [KnownPath.MyDocumentsDirectory] = Home / "Documents",
                [KnownPath.MyGamesDirectory] = Home / "Documents" / "My Games",
                [KnownPath.ApplicationDataDirectory] = Home / ".config",
                [KnownPath.LocalApplicationDataDirectory] = Home / ".local" / "share",
                [KnownPath.CommonApplicationDataDirectory] = _root / "common-app-data",
            });

        (SteamRoot / "steam.sh").Parent.CreateDirectory();
        File.WriteAllText((SteamRoot / "steam.sh").ToString(), "");
        (SteamRoot / "steamapps").CreateDirectory();
        DepotCache.CreateDirectory();
        (Library / "steamapps").CreateDirectory();
    }

    /// <summary>The temp folder everything lives in, for a test that wants its own files beside the library.</summary>
    public AbsolutePath Root => _root;

    public AbsolutePath Home { get; }

    /// <summary>The Steam client root, <c>~/.steam/steam</c> of the fake home.</summary>
    public AbsolutePath SteamRoot { get; }

    /// <summary>The second library, reached through the symlinked <c>mnt</c> folder.</summary>
    public AbsolutePath Library { get; }

    public AbsolutePath DepotCache => SteamRoot / "depotcache";

    public AbsolutePath LibraryDepotCache => Library / "steamapps" / "depotcache";

    /// <summary>The file system to hand to the locators: the real disk with every known folder inside the fixture.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>
    /// Installs a game in the library: the game folder with the files of every depot, the app manifest listing
    /// the depots, and each depot's manifest in the depotcache that <see cref="SteamDepot.Cache"/> names.
    /// The library list is rewritten to include the app, and the game gets the Proton prefix of a game that
    /// has been started once.
    /// </summary>
    public SteamInstall AddGame(uint appId, string installDir, string name, ulong buildId, params SteamDepot[] depots)
    {
        var folder = Library / "steamapps" / "common" / installDir;
        folder.CreateDirectory();
        foreach (var depot in depots)
        {
            foreach (var file in depot.Files)
                WriteFile(folder, file.Path, file.Content);
            WriteManifest(depot);
        }

        var appManifest = Library / "steamapps" / $"appmanifest_{appId}.acf";
        File.WriteAllText(appManifest.ToString(), AppManifest(appId, installDir, name, buildId, depots));

        // A Windows game Steam has run once: Proton has written its registry and the user's folders.
        var prefix = Library / "steamapps" / "compatdata" / appId.ToString() / "pfx";
        (prefix / "drive_c" / "users" / "steamuser" / "AppData" / "Local").CreateDirectory();
        (prefix / "drive_c" / "users" / "steamuser" / "Documents" / "My Games").CreateDirectory();
        File.WriteAllText((prefix / "system.reg").ToString(), "WINE REGISTRY Version 2");

        var install = new SteamInstall(appId, installDir, folder, appManifest);
        _installs.Add(install);
        WriteLibraryFolders();
        return install;
    }

    /// <summary>
    /// Saves a manifest of a depot the install has since moved past, beside the current one, like the old
    /// manifests Steam leaves in its depotcache after an update.
    /// </summary>
    public void AddSupersededManifest(SteamDepot depot) => WriteManifest(depot);

    /// <summary>Saves the manifest of a depot again, in the depotcache its <see cref="SteamDepot.Cache"/> names.</summary>
    public void RestoreManifest(SteamDepot depot) => WriteManifest(depot);

    /// <summary>Deletes the saved manifest of a depot, as Steam clearing its cache would.</summary>
    public void RemoveManifest(uint depotId, ulong manifestId)
    {
        foreach (var directory in new[] { DepotCache, LibraryDepotCache })
        {
            var file = directory / $"{depotId}_{manifestId}.manifest";
            if (file.FileExists) file.Delete();
        }
    }

    /// <summary>Puts a file in the game folder that no depot ships: a mod file, a log, a Creation Club item.</summary>
    public static void WriteExtra(SteamInstall install, string path, byte[] content) => WriteFile(install.Folder, path, content);

    public static void WriteExtra(SteamInstall install, string path, string text) => WriteFile(install.Folder, path, Encoding.UTF8.GetBytes(text));

    private static void WriteFile(AbsolutePath folder, string path, byte[] content)
    {
        var target = folder / path;
        target.Parent.CreateDirectory();
        File.WriteAllBytes(target.ToString(), content);
    }

    private void WriteManifest(SteamDepot depot)
    {
        var directory = depot.Cache switch
        {
            ManifestCache.ClientRoot => DepotCache,
            ManifestCache.Library => LibraryDepotCache,
            _ => default,
        };
        if (depot.Cache == ManifestCache.None) return;

        DepotManifestFixture.Write(
            directory / $"{depot.DepotId}_{depot.ManifestId}.manifest",
            DepotId.From(depot.DepotId),
            ManifestId.From(depot.ManifestId),
            depot.Files.Select(f => (f.Path, f.Content)));
    }

    private void WriteLibraryFolders()
    {
        var text = new StringBuilder();
        text.Append("\"libraryfolders\"\n{\n");
        AppendLibrary(text, "0", SteamRoot, []);
        AppendLibrary(text, "1", Library, _installs.Select(i => i.AppId));
        text.Append("}\n");

        // The client keeps one copy in config/ and one in steamapps/.
        foreach (var path in new[] { SteamRoot / "config" / "libraryfolders.vdf", SteamRoot / "steamapps" / "libraryfolders.vdf" })
        {
            path.Parent.CreateDirectory();
            File.WriteAllText(path.ToString(), text.ToString());
        }
    }

    private static void AppendLibrary(StringBuilder text, string index, AbsolutePath path, IEnumerable<uint> appIds)
    {
        text.Append($"\t\"{index}\"\n\t{{\n");
        text.Append($"\t\t\"path\"\t\t\"{path}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"contentid\"\t\t\"{LibraryContentId}{index}\"\n\t\t\"totalsize\"\t\t\"0\"\n");
        text.Append("\t\t\"apps\"\n\t\t{\n");
        foreach (var appId in appIds) text.Append($"\t\t\t\"{appId}\"\t\t\"1\"\n");
        text.Append("\t\t}\n\t}\n");
    }

    private static string AppManifest(uint appId, string installDir, string name, ulong buildId, IReadOnlyList<SteamDepot> depots)
    {
        var text = new StringBuilder();
        text.Append("\"AppState\"\n{\n");
        text.Append($"\t\"appid\"\t\t\"{appId}\"\n\t\"Universe\"\t\t\"1\"\n\t\"name\"\t\t\"{name}\"\n");
        text.Append($"\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{installDir}\"\n");
        text.Append($"\t\"LastOwner\"\t\t\"76561198000000000\"\n\t\"BuildID\"\t\t\"{buildId}\"\n");
        text.Append("\t\"InstalledDepots\"\n\t{\n");
        foreach (var depot in depots)
        {
            var size = depot.Files.Sum(f => f.Content.LongLength);
            text.Append($"\t\t\"{depot.DepotId}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"{depot.ManifestId}\"\n\t\t\t\"size\"\t\t\"{size}\"\n\t\t}}\n");
        }

        text.Append("\t}\n}\n");
        return text.ToString();
    }

    public void Dispose()
    {
        // The symlink goes first: a recursive delete must not be asked to follow it.
        var link = _root / "mnt";
        if (Directory.Exists(link.ToString())) Directory.Delete(link.ToString(), recursive: false);
        if (Directory.Exists(_root.ToString())) Directory.Delete(_root.ToString(), recursive: true);
    }
}
