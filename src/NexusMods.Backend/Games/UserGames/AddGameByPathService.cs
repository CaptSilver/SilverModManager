using System.Runtime.InteropServices;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Models;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.UserGames;

/// <inheritdoc />
internal sealed class AddGameByPathService : IAddGameByPath
{
    private readonly IGameRegistry _registry;
    private readonly IUserGameInstallStore _store;
    private readonly ILogger _logger;
    private readonly Lazy<(IGameData Game, RelativePath File)[]> _launchers;

    public AddGameByPathService(IEnumerable<IGameData> games, IGameRegistry registry, IUserGameInstallStore store, IFileSystem fileSystem, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _store = store;
        _logger = loggerFactory.CreateLogger<AddGameByPathService>();

        // The probe only needs a locator to fill in a field no game reads, and the user-install locator is the
        // one that would own this result.
        var locator = new UserInstallLocator(loggerFactory.CreateLogger<UserInstallLocator>(), store, fileSystem, games);
        var all = games.ToArray();
        _launchers = new Lazy<(IGameData, RelativePath)[]>(() => all
            .SelectMany(game => PrimaryFileProbe.Find(game, fileSystem, locator, _logger) is { } file
                ? [(game, file)]
                : Array.Empty<(IGameData, RelativePath)>())
            .ToArray());
    }

    public AddGameResult Add(AbsolutePath folder, GameId? game, AbsolutePath? prefix, AbsolutePath? wine)
    {
        // Checked before anything is narrowed: a folder Steam already found is reported as found, not as
        // ambiguous, and a second add of the same folder must not append a duplicate record.
        foreach (var installation in _registry.LocateGameInstallations())
        {
            if (InstallPaths.AreSameFolder(installation.LocatorResult.Path.GetFullPath(), folder))
                return new AddGameResult.AlreadyDetected(installation.Game, installation.LocatorResult.Store);
        }

        // Built before the try: only reading the given folder should come back as FolderUnreadable.
        var launchers = _launchers.Value;
        List<(IGameData Game, RelativePath File)> candidates;
        try
        {
            EnsureListable(folder);
            candidates = launchers.Where(entry => FindIgnoringCase(folder, entry.File) is not null).ToList();
            if (game is { } wanted) candidates = candidates.Where(entry => entry.Game.GameId == wanted).ToList();
            if (candidates.Count > 1) candidates = NarrowByIdentifyingFiles(folder, candidates);
            if (candidates.Count > 1) candidates = NarrowByStore(folder, candidates);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not list '{Folder}'", folder);
            return new AddGameResult.FolderUnreadable(folder, e.Message);
        }

        if (candidates.Count == 0) return new AddGameResult.Unknown();
        if (candidates.Count > 1) return new AddGameResult.Ambiguous(candidates.Select(entry => new AddGameResult.AmbiguousCandidate(entry.Game, entry.File)).ToArray());

        var chosen = candidates[0].Game;
        if (!chosen.HasNativeLinuxBuild && prefix is null) return new AddGameResult.NeedsPrefix(chosen);

        var platform = chosen.HasNativeLinuxBuild ? OSPlatform.Linux : OSPlatform.Windows;
        try
        {
            _store.Add(new UserGameInstall(chosen.GameId, folder.ToString(), platform, prefix?.ToString(), wine?.ToString()));
        }
        catch (InvalidOperationException e)
        {
            return new AddGameResult.StoreUnavailable(e.Message);
        }

        _registry.ClearCache();
        return new AddGameResult.Added(chosen);
    }

    /// <summary>
    /// Throws when the folder is on disk but cannot be listed. The file system layer skips inaccessible
    /// entries silently, so without this a folder the user cannot read is reported as having no known game in it.
    /// </summary>
    private static void EnsureListable(AbsolutePath folder)
    {
        // Paths that are not on the real disk (the in-memory file system) have nothing to check.
        var path = folder.ToString();
        if (!Directory.Exists(path)) return;
        using var entries = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = false }).GetEnumerator();
        entries.MoveNext();
    }

    /// <summary>
    /// Drops the games that name identifying files the folder lacks. Games that name files and have them win over
    /// games that name none, so an Enderal folder is not also a Skyrim one.
    /// </summary>
    private static List<(IGameData Game, RelativePath File)> NarrowByIdentifyingFiles(AbsolutePath folder, List<(IGameData Game, RelativePath File)> candidates)
    {
        static IReadOnlyList<RelativePath> Declared(IGameData game) => (game as IGameIdentifyingFiles)?.IdentifyingFiles ?? [];

        var remaining = candidates
            .Where(entry => Declared(entry.Game).All(file => FindIgnoringCase(folder, file) is not null))
            .ToList();

        var identified = remaining.Where(entry => Declared(entry.Game).Count > 0).ToList();
        return identified.Count > 0 ? identified : remaining.Where(entry => Declared(entry.Game).Count == 0).ToList();
    }

    /// <summary>
    /// Uses what the store left in or next to the folder: a Steam app manifest naming it as its install directory,
    /// or a GOG info file. Keeps every candidate when the store data decides nothing.
    /// </summary>
    private List<(IGameData Game, RelativePath File)> NarrowByStore(AbsolutePath folder, List<(IGameData Game, RelativePath File)> candidates)
    {
        var steamApps = folder.Parent.Parent;
        if (folder.Parent.FileName == "common" && steamApps.FileName == "steamapps" && steamApps.DirectoryExists())
        {
            foreach (var manifestPath in steamApps.EnumerateFiles("*", recursive: false).Where(IsAppManifest))
            {
                var manifest = ParseManifest(manifestPath);
                if (manifest is null || !string.Equals(manifest.InstallationDirectory.FileName, folder.FileName, StringComparison.Ordinal)) continue;

                var matches = candidates.Where(entry => entry.Game.StoreIdentifiers.SteamAppIds.Contains(manifest.AppId.Value)).ToList();
                if (matches.Count > 0) return matches;
            }
        }

        foreach (var info in folder.EnumerateFiles("*", recursive: false).Where(IsGogInfo))
        {
            var name = info.FileName;
            if (!long.TryParse(name["goggame-".Length..^".info".Length], out var productId)) continue;

            var matches = candidates.Where(entry => entry.Game.StoreIdentifiers.GOGProductIds.Contains(productId)).ToList();
            if (matches.Count > 0) return matches;
        }

        return candidates;
    }

    private static bool IsAppManifest(AbsolutePath file)
        => file.FileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && file.FileName.EndsWith(".acf", StringComparison.OrdinalIgnoreCase);

    private static bool IsGogInfo(AbsolutePath file)
        => file.FileName.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && file.FileName.EndsWith(".info", StringComparison.OrdinalIgnoreCase);

    private AppManifest? ParseManifest(AbsolutePath path)
    {
        try
        {
            var manifest = AppManifestParser.ParseManifestFile(path);
            if (!manifest.IsFailed) return manifest.Value;

            _logger.LogWarning("Failed to parse app manifest file '{Path}': {Errors}", path, manifest.Errors);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Skipping unreadable app manifest '{Path}'", path);
        }

        return null;
    }

    /// <summary>
    /// The file at <paramref name="file"/> below <paramref name="folder"/>, matching each name exactly first and
    /// ignoring case otherwise: a Windows game's files are spelled however its installer spelled them.
    /// </summary>
    private static AbsolutePath? FindIgnoringCase(AbsolutePath folder, RelativePath file)
    {
        var current = folder;
        var parts = file.Parts.ToArray();
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var exact = current.Combine(parts[i]);
            if (last ? exact.FileExists : exact.DirectoryExists())
            {
                current = exact;
                continue;
            }

            if (!current.DirectoryExists()) return null;
            // Only this folder's own entries: the listing is recursive by default, and a same-named file deeper
            // down (a backup copy, a bundled tool) is not the one the game launches.
            var entries = last ? current.EnumerateFiles("*", recursive: false) : current.EnumerateDirectories("*", recursive: false);
            var name = parts[i].ToString();
            var matches = entries.Where(entry => string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
            if (matches.Length == 0) return null;
            current = matches[0];
        }

        return current;
    }
}
