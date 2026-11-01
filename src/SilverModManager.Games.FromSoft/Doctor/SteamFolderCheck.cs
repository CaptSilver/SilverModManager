using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>What differs between the real game folder and Steam's manifest, as paths relative to the game folder.</summary>
/// <param name="Changed">Files the manifest lists whose size or SHA-1 is not the listed one.</param>
/// <param name="ModFiles">Files and folders that only mods put in the game folder.</param>
public sealed record SteamFolderFindings(IReadOnlyList<string> Changed, IReadOnlyList<string> ModFiles)
{
    public bool IsEmpty => Changed.Count == 0 && ModFiles.Count == 0;
}

/// <summary>
/// Compares the real game folder with the file list of the Steam manifest the install is at. Size is compared first and
/// is enough for a file that differs. The regulation and the executables are always hashed, because those are what a
/// Steam launch loads online. Any other file is hashed only when its size and modified time are not the ones a
/// verdict was cached for, so a repeat run reads nothing; archives (see <see cref="FromSoftPaths.IsArchiveOrMedia"/>)
/// run to tens of gigabytes that mods do not rewrite in place, so they are compared by size alone.
/// </summary>
/// <remarks>
/// The verdicts live in memory. A run after a restart hashes the small files once more, which is cheap since the
/// archives are not among them.
/// </remarks>
public sealed class SteamFolderCheck(ILogger<SteamFolderCheck> logger)
{
    private readonly ConcurrentDictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);

    private readonly record struct Verdict(long Size, long ModifiedTicks, Sha1Value Expected, bool Matches);

    /// <param name="installRoot">The install folder the manifest's paths are relative to; the game's files sit in its <see cref="FromSoftGameLayout.GameSubfolder"/>.</param>
    /// <param name="manifest">The files of the installed build. Entries without a path (encrypted names) leave only the regulation and the executables to compare, by hash against the whole list.</param>
    public async Task<SteamFolderFindings> Check(FromSoftGameDefinition definition, AbsolutePath installRoot, IReadOnlyList<ManifestFile> manifest, CancellationToken cancellationToken = default)
    {
        var layout = definition.Layout;
        var gameFolder = installRoot / layout.GameSubfolder;
        if (!gameFolder.DirectoryExists()) return new SteamFolderFindings([], []);

        var changed = manifest.Any(f => f.Path is null)
            ? await ChangedByHash(definition, gameFolder, manifest, cancellationToken)
            : await ChangedByName(definition, installRoot, manifest, cancellationToken);

        return new SteamFolderFindings(changed.Order(StringComparer.Ordinal).ToArray(), FromSoftChecks.ModArtifacts(gameFolder, definition.CheckRules));
    }

    private async Task<List<string>> ChangedByName(FromSoftGameDefinition definition, AbsolutePath installRoot, IReadOnlyList<ManifestFile> manifest, CancellationToken cancellationToken)
    {
        var layout = definition.Layout;
        var prefix = layout.GameSubfolder + "/";
        var changed = new List<string>();
        foreach (var file in manifest)
        {
            if (file.Path is not { } name) continue;

            // Steam writes Windows separators in a depot's file names.
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var inside = path[prefix.Length..];
            var archive = FromSoftPaths.IsArchiveOrMedia(layout, new GamePath(LocationId.Game, path));
            if (await Differs(installRoot / path, file, archive, Pinned(definition, inside), cancellationToken)) changed.Add(inside);
        }
        return changed;
    }

    // Names are hidden, so the best a file can be held to is some file of the manifest having its hash.
    private async Task<List<string>> ChangedByHash(FromSoftGameDefinition definition, AbsolutePath gameFolder, IReadOnlyList<ManifestFile> manifest, CancellationToken cancellationToken)
    {
        var listed = manifest.Select(f => f.Hash).ToHashSet();
        var changed = new List<string>();
        string?[] names = [definition.Formats.RegulationFileName, definition.Layout.GameExecutable, definition.Layout.Launcher];
        foreach (var name in names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = gameFolder / name;
            if (await HashOf(file, cancellationToken) is { } hash && !listed.Contains(hash)) changed.Add(name);
        }
        return changed;
    }

    private static bool Pinned(FromSoftGameDefinition definition, string inside)
        => inside.Equals(definition.Formats.RegulationFileName, StringComparison.OrdinalIgnoreCase)
           || inside.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> Differs(AbsolutePath path, ManifestFile expected, bool sizeOnly, bool pinned, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.GetFullPath());
        if (!info.Exists) return false;
        if ((ulong)info.Length != expected.Size) return true;
        if (sizeOnly) return false;
        if (pinned) return await HashOf(path, cancellationToken) is { } hash && hash != expected.Hash;

        var key = path.GetFullPath();
        if (_verdicts.TryGetValue(key, out var known) && known.Size == info.Length && known.ModifiedTicks == info.LastWriteTimeUtc.Ticks && known.Expected == expected.Hash)
            return !known.Matches;

        if (await HashOf(path, cancellationToken) is not { } actual) return false;
        var matches = actual == expected.Hash;
        _verdicts[key] = new Verdict(info.Length, info.LastWriteTimeUtc.Ticks, expected.Hash, matches);
        return !matches;
    }

    /// <summary>The SHA-1 of the file, or null when it is missing or cannot be read; an unreadable file is logged and not called changed.</summary>
    private async Task<Sha1Value?> HashOf(AbsolutePath path, CancellationToken cancellationToken)
    {
        try
        {
            if (!path.FileExists) return null;
            return await FileHashing.Digest<Sha1Value, SHA1, Sha1Hasher>(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "{Path} could not be read to compare it with Steam's manifest", path);
            return null;
        }
    }
}
