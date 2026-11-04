using System.Collections.Immutable;
using DynamicData.Kernel;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Games;

/// <summary>
/// Reads cover art out of Steam's own <c>appcache/librarycache</c> folder. Steam fills it
/// when the library is browsed, so it only ever holds art the user's client already
/// downloaded. Answers null for a game Steam has not cached.
/// </summary>
internal sealed class SteamLibraryCacheArtSource : IGameArtProvider
{
    private const string TileFileName = "library_600x900.jpg";

    // Newer clients name the portrait cover library_capsule.jpg instead; same aspect, so it
    // fills the tile just as well. Listed in order of preference.
    private static readonly string[] CoverFileNames = [TileFileName, "library_capsule.jpg"];

    // Besides the covers, Steam keeps these named files in an app folder. The one remaining
    // .jpg at the top level, named by a hash, is the 32x32 client icon.
    private static readonly string[] StandardJpgNames = ["header.jpg", "library_header.jpg", "library_hero.jpg", "library_hero_blur.jpg", ..CoverFileNames];

    private readonly Func<IEnumerable<AbsolutePath>> _steamRoots;

    public SteamLibraryCacheArtSource(IFileSystem fileSystem)
        : this(() => FindSteamRoots(fileSystem)) { }

    public SteamLibraryCacheArtSource(Func<IEnumerable<AbsolutePath>> steamRoots) => _steamRoots = steamRoots;

    public ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request)
    {
        var cover = FindCover(_steamRoots().ToArray(), request.SteamAppIds);
        return ValueTask.FromResult<IStreamFactory?>(cover.HasValue ? new NativeFileStreamFactory(cover.Value) : null);
    }

    public ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request)
    {
        var roots = _steamRoots().ToArray();

        // A real icon from any id beats a cover from an earlier one: the cover is the
        // fallback, not a peer, because a portrait picture in a square slot is the worse result.
        foreach (var appId in request.SteamAppIds)
        {
            foreach (var folder in CacheFolders(roots, appId))
            {
                var icon = FindClientIcon(folder);
                if (icon.HasValue) return ValueTask.FromResult<IStreamFactory?>(new NativeFileStreamFactory(icon.Value));
            }
        }

        var cover = FindCover(roots, request.SteamAppIds);
        return ValueTask.FromResult<IStreamFactory?>(cover.HasValue ? new NativeFileStreamFactory(cover.Value) : null);
    }

    private static IEnumerable<AbsolutePath> FindSteamRoots(IFileSystem fileSystem)
    {
        // No registry on Linux; GameFinder looks in the known XDG and Flatpak locations.
        return SteamLocationFinder.TryFindSteam(fileSystem, registry: null, NullLogger.Instance, out var steam) ? [steam] : [];
    }

    private static Optional<AbsolutePath> FindCover(AbsolutePath[] roots, ImmutableArray<uint> steamAppIds)
    {
        foreach (var appId in steamAppIds)
        {
            foreach (var folder in CacheFolders(roots, appId))
            {
                // Older clients write the cover straight into the app folder; newer ones put each
                // asset in its own hash-named subfolder, so look one level down as well.
                var places = folder.EnumerateDirectories(recursive: false).Prepend(folder).ToArray();
                foreach (var name in CoverFileNames)
                {
                    foreach (var place in places)
                    {
                        var cover = place.Combine(name);
                        if (cover.FileExists) return cover;
                    }
                }
            }
        }

        return Optional<AbsolutePath>.None;
    }

    private static IEnumerable<AbsolutePath> CacheFolders(AbsolutePath[] roots, uint appId)
    {
        return roots
            .Select(root => root.Combine("appcache").Combine("librarycache").Combine(appId.ToString()))
            .Where(folder => folder.DirectoryExists());
    }

    private static Optional<AbsolutePath> FindClientIcon(AbsolutePath folder)
    {
        var candidates = folder
            .EnumerateFiles("*.jpg", recursive: false)
            .Where(file => !StandardJpgNames.Contains(file.FileName, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // More than one unexplained .jpg means the layout is not what this rule was written
        // for, so use the cover instead of guessing which is the icon.
        return candidates.Length == 1 ? candidates[0] : Optional<AbsolutePath>.None;
    }
}
