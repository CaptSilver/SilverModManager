using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Paths;
using SteamKit2;

namespace NexusMods.Networking.Steam;

/// <summary>
/// Reads the manifest files the Steam client keeps in its depotcache folder.
/// </summary>
internal sealed class LocalManifestReader
{
    public LocalManifestResult Read(AbsolutePath manifestPath)
    {
        DepotManifest? manifest;
        try
        {
            manifest = DepotManifest.LoadFromFile(manifestPath.ToString());
        }
        catch (Exception e)
        {
            return new LocalManifestResult.Unreadable(e.Message);
        }

        if (manifest is null)
            return new LocalManifestResult.Unreadable($"'{manifestPath}' is not a depot manifest");

        // Names are ciphertext until the depot key is applied, so nothing below may look at them.
        if (manifest.FilenamesEncrypted)
            return new LocalManifestResult.NeedsDepotKey(DepotId.From(manifest.DepotID), ManifestId.From(manifest.ManifestGID));

        // The baseline lists files on disk. Directory and symlink entries have none, and ManifestParser is shared
        // with the CDN download path (which must keep seeing every entry), so they are dropped here.
        // Other flags (InstallScript, Executable, ...) mark real files and stay.
        manifest.Files = manifest.Files?.Where(f => !IsDirectoryOrSymlink(f)).ToList();

        try
        {
            return new LocalManifestResult.Parsed(ManifestParser.Parse(manifest));
        }
        catch (Exception e)
        {
            return new LocalManifestResult.Unreadable(e.Message);
        }
    }

    private static bool IsDirectoryOrSymlink(DepotManifest.FileData file) =>
        file.Flags.HasFlag(EDepotFileFlag.Directory)
        || file.Flags.HasFlag(EDepotFileFlag.Symlink)
        || !string.IsNullOrEmpty(file.LinkTarget);
}
