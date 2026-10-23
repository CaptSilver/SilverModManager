using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>
/// Reads the installed build for the merge path, which asks for it several times in one Apply (choosing a vanilla
/// dump, vouching for regulations, adopting a baseline). <see cref="BuildIdentity.Identify"/> decrypts and
/// decompresses the whole regulation, so a reading is kept until the regulation, the executable or Steam's build id
/// changes. Callers that must see the disk as it is now (heal, stamping) call <see cref="BuildIdentity.Identify"/>
/// themselves.
/// </summary>
public class InstalledBuilds
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Key, BuildIdentity Identity)> _byFolder = new(StringComparer.Ordinal);

    /// <summary>The installed build; throws <see cref="InvalidOperationException"/> like <see cref="BuildIdentity.Identify"/> when it cannot be read.</summary>
    public BuildIdentity Identify(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game) return Read(installation);

        var folder = FromSoftPaths.GameFolder(game.Layout, installation);
        var key = string.Join('|',
            installation.LocatorResult.StoreBuildId,
            Stamp(folder / game.Layout.GameExecutable),
            game.Formats.RegulationFileName is { } name ? Stamp(folder / name) : "none");
        var folderKey = folder.GetFullPath();

        lock (_gate)
        {
            if (_byFolder.TryGetValue(folderKey, out var cached) && cached.Key == key) return cached.Identity;

            var identity = Read(installation);
            _byFolder[folderKey] = (key, identity);
            return identity;
        }
    }

    /// <summary>Reads the build from disk. The seam for a test that counts readings.</summary>
    protected virtual BuildIdentity Read(GameInstallation installation) => BuildIdentity.Identify(installation);

    // Path, size and write time: a rewritten file moves at least one of them, and no content is read to find out.
    private static string Stamp(AbsolutePath file)
    {
        var info = new FileInfo(file.GetFullPath());
        return info.Exists ? $"{info.FullName}/{info.Length}/{info.LastWriteTimeUtc.Ticks}" : $"{info.FullName}/missing";
    }
}
