using System.Security.Cryptography;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;
using SteamKit2;

namespace SilverModManager.Networking.Steam.Tests;

/// <summary>
/// Writes manifests in the four-section binary format Steam keeps in its depotcache folder
/// (payload, metadata, signature, end marker), so reader tests never need a real Steam install.
/// SteamKit's own writer produces that layout, which <see cref="PayloadMagic"/> and
/// <see cref="EndMagic"/> let tests confirm byte-wise.
/// </summary>
public static class DepotManifestFixture
{
    public const uint PayloadMagic = 0x71F617D0;
    public const uint EndMagic = 0x32C415AB;

    /// <summary>
    /// One entry to write: a regular file by default, or a directory or symlink via <paramref name="Flags"/>.
    /// </summary>
    public record Entry(string Path, byte[] Content, EDepotFileFlag Flags = 0, string LinkTarget = "");

    public static void Write(AbsolutePath path, DepotId depot, ManifestId id, IEnumerable<(string Path, byte[] Content)> files, bool encryptFilenames = false)
    {
        Write(path, depot, id, files.Select(f => new Entry(f.Path, f.Content)), encryptFilenames);
    }

    public static void Write(AbsolutePath path, DepotId depot, ManifestId id, IEnumerable<Entry> entries, bool encryptFilenames = false)
    {
        var manifest = new DepotManifest
        {
            DepotID = depot.Value,
            ManifestGID = id.Value,
            CreationTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            FilenamesEncrypted = encryptFilenames,
            Files = entries.Select(e => ToFileData(e, encryptFilenames)).ToList(),
        };

        path.Parent.CreateDirectory();
        manifest.SaveToFile(path.ToString());
    }

    private static DepotManifest.FileData ToFileData(Entry entry, bool encrypt)
    {
        // Steam writes backslash separators regardless of the depot's target OS.
        var name = entry.Path.Replace('/', '\\');
        if (encrypt)
            name = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

        return new DepotManifest.FileData(
            name,
            SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name)),
            entry.Flags,
            (ulong)entry.Content.Length,
            SHA1.HashData(entry.Content),
            entry.LinkTarget,
            encrypt,
            0
        );
    }
}
