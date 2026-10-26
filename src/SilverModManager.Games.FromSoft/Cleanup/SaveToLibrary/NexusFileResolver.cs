using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>Either the file an entry stands for or the reason it could not be told.</summary>
public sealed record FileResolution(ModToSave? Mod, string? Why);

/// <summary>
/// Turns a lock entry into the Nexus Mods file it was installed from. A file id in the lock is taken as it is. Without
/// one the mod page comes from the archive name and the file is chosen from the page by name, then by version, then by
/// upload time; if that still leaves several, the entry is reported instead of guessed.
/// </summary>
public sealed class NexusFileResolver(INexusCatalog catalog, ILogger? logger = null)
{
    public async Task<FileResolution> ResolveAsync(NexusModsGameId game, ProvenanceEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveCoreAsync(game, entry, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning(e, "Looking up {Mod} on Nexus Mods failed", entry.Id);
            return Unresolved($"Nexus Mods could not be asked about it: {e.Message}");
        }
    }

    private async Task<FileResolution> ResolveCoreAsync(NexusModsGameId game, ProvenanceEntry entry, CancellationToken cancellationToken)
    {
        var archive = entry.Asset is null ? null : ArchiveName.TryParse(entry.Asset, entry.Version);
        var modId = entry.ModId ?? archive?.ModId;

        if (entry.FileId is { } fileId)
        {
            modId ??= await catalog.ModOfFileAsync(game, fileId, cancellationToken);
            return modId is null
                ? Unresolved($"Nexus Mods has no file {fileId}.")
                : new FileResolution(new ModToSave(entry.Id, entry.Id, entry.Version ?? archive?.Version ?? "", modId.Value, fileId), null);
        }

        if (archive is null && entry.ModId is { } knownMod)
        {
            var onPage = await catalog.ListFilesAsync(game, knownMod, cancellationToken);
            return onPage is null ? Unresolved($"Nexus Mods has no mod {knownMod}.") : ChooseByVersion(entry, knownMod, onPage);
        }

        if (archive is null) return Unresolved("The lock has no file id and its archive name does not say which Nexus Mods page it came from.");

        var files = await catalog.ListFilesAsync(game, archive.ModId, cancellationToken);
        if (files is null) return Unresolved($"Nexus Mods has no mod {archive.ModId}.");

        return Choose(entry, archive, files);
    }

    private static FileResolution Choose(ProvenanceEntry entry, ArchiveName archive, IReadOnlyList<NexusFile> files)
    {
        var pool = files;

        var named = pool.Where(f => Squash(f.Name) == Squash(archive.Name)).ToArray();
        if (named.Length > 0) pool = named;
        if (pool.Count == 1 && named.Length == 1) return Found(entry, archive.ModId, pool[0]);

        var version = ArchiveName.Dashed(entry.Version ?? archive.Version);
        var versioned = pool.Where(f => ArchiveName.Dashed(f.Version) == version).ToArray();
        if (versioned.Length == 0) return Unresolved($"No file of mod {archive.ModId} has version {entry.Version ?? archive.Version}.");
        if (versioned.Length == 1) return Found(entry, archive.ModId, versioned[0]);

        var uploaded = versioned.Where(f => SameUpload(f.Uploaded, archive)).ToArray();
        return uploaded.Length == 1
            ? Found(entry, archive.ModId, uploaded[0])
            : Unresolved($"Fits more than one file of mod {archive.ModId} ({string.Join(", ", versioned.Select(f => $"{f.Name} (file {f.FileId})"))}) and the upload time does not settle it.");
    }

    // A record that names the mod page and a version, but no file: the one file of that version is the one installed.
    private static FileResolution ChooseByVersion(ProvenanceEntry entry, uint modId, IReadOnlyList<NexusFile> files)
    {
        if (string.IsNullOrWhiteSpace(entry.Version)) return Unresolved($"The record names mod {modId} but no file and no version.");

        var version = ArchiveName.Dashed(entry.Version);
        var versioned = files.Where(f => ArchiveName.Dashed(f.Version) == version).ToArray();
        return versioned.Length switch
        {
            1 => Found(entry, modId, versioned[0]),
            0 => Unresolved($"No file of mod {modId} has version {entry.Version}."),
            _ => Unresolved($"More than one file of mod {modId} has version {entry.Version} ({string.Join(", ", versioned.Select(f => $"{f.Name} (file {f.FileId})"))})."),
        };
    }

    private static FileResolution Found(ProvenanceEntry entry, uint modId, NexusFile file)
        => new(new ModToSave(entry.Id, file.Name, file.Version, modId, file.FileId), null);

    private static FileResolution Unresolved(string why) => new(null, why);

    private static bool SameUpload(long unixSeconds, ArchiveName archive)
    {
        var wanted = archive.Uploaded.ToUnixTimeSeconds();
        return archive.MinuteResolution ? unixSeconds / 60 == wanted / 60 : Math.Abs(unixSeconds - wanted) <= 1;
    }

    /// <summary>A name with everything but letters and digits dropped, so "Adjust The FOV" and "AdjustTheFov" compare equal.</summary>
    internal static string Squash(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
