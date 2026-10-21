using System.Text;
using JetBrains.Annotations;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Library.Lock;

/// <summary>
/// Turns the mods of a loadout into a <see cref="LockFile"/>.
/// </summary>
[PublicAPI]
public sealed class LockExport(LibraryFileDigests digests)
{
    /// <summary>
    /// Exports every enabled group that links a Nexus Mods or GitHub release file. A group that links anything else, or
    /// a file whose digest cannot be established, fails the export: a lock that quietly left a mod out, or pinned it
    /// without a digest, would be worse than none.
    /// </summary>
    public async ValueTask<LockFile> FromLoadout(Loadout.ReadOnly loadout, CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, LockEntry>(StringComparer.Ordinal);

        var linked = LibraryLinkedLoadoutItem.All(loadout.Rebase().Db)
            .Where(item => item.AsLoadoutItem().LoadoutId == loadout.LoadoutId && item.AsLoadoutItem().IsEnabled())
            .OrderBy(item => item.AsLoadoutItem().Name, StringComparer.Ordinal);

        foreach (var item in linked)
        {
            var groupName = item.AsLoadoutItem().Name;
            var libraryItem = item.LibraryItem;
            if (!libraryItem.TryGetAsLibraryFile(out var file))
                throw new LockException($"'{groupName}' is not linked to a downloaded file, so there is nothing to pin.");

            var entry = await ToEntry(groupName, libraryItem, file, cancellationToken);
            if (!entries.TryAdd(entry.Id, entry))
                throw new LockException($"'{groupName}' ({file.FileName}) and '{entries[entry.Id].Asset}' both become the lock id '{entry.Id}'.");
        }

        return new LockFile(entries.Values.ToArray());
    }

    private async ValueTask<LockEntry> ToEntry(string groupName, LibraryItem.ReadOnly libraryItem, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        var asset = file.FileName.ToString();

        if (libraryItem.TryGetAsNexusModsLibraryItem(out var nexus))
        {
            var sha256 = await digests.EnsureSha256(file, cancellationToken)
                ?? throw new LockException($"'{asset}' has no SHA-256 and its bytes are not in the file store, so it cannot be pinned.");

            var id = SanitizeId(nexus.ModPageMetadata.Name, asset);
            return new LockEntry(id, nexus.FileMetadata.Version, nexus.FileMetadata.Uid.FileId.Value.ToString(), asset, sha256.ToString(), LockSources.Nexus);
        }

        if (file.TryGetAsGitHubReleaseLibraryFile(out var release))
            return new LockEntry(SanitizeId(groupName, asset), release.Tag, null, release.AssetName, release.PinnedSha256.ToString(), LockSources.GitHub);

        throw new LockException($"'{asset}' ('{groupName}') came from neither Nexus Mods nor a GitHub release, so it cannot be pinned.");
    }

    /// <summary>
    /// A lower case id made of letters, digits and single dashes, which is a bare TOML key and reads the same in erm.
    /// </summary>
    internal static string SanitizeId(string name, string file)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var id = builder.ToString().TrimEnd('-');
        return id.Length > 0 ? id : throw new LockException($"'{name}' ({file}) has no letters or digits to make a lock id from.");
    }
}
