using System.Text;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>
/// The user's openmw.cfg on the host. OpenMW reads its load order from there, not from the Proton prefix, so
/// Apply rewrites the <c>content=</c> lines for the plugins under Data Files (see <see cref="OpenMwContentWriter"/>).
/// </summary>
/// <remarks>
/// Safe to read the target while writing it: every deployment backend writes intrinsic files through the copy
/// backend, which renders them into memory and only then replaces the file on disk.
/// </remarks>
public class OpenMwConfigFile(IBethesdaGame game) : IIntrinsicFile
{
    private static readonly HashSet<Extension> PluginExtensions =
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP,
        KnownCEExtensions.OMWGame, KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWScripts,
    ];

    private static readonly byte[] Utf8Bom = Encoding.UTF8.GetPreamble();

    public GamePath Path { get; } = new(Morrowind.OpenMwConfig, OpenMwConfigLocation.FileName);

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var (owned, deployed) = PluginNames(syncTree);

        var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        var existing = onDisk.FileExists ? await onDisk.ReadAllBytesAsync() : [];

        await stream.WriteAsync(Rewrite(existing, deployed, owned));
        await stream.FlushAsync();
    }

    /// <remarks>
    /// The synchronizer hands a changed intrinsic file to Ingest and only writes it on the sync after, and by
    /// then the files of a mod that was just removed are gone from the tree, so Write could no longer tell that
    /// their entries are ours. Ingest therefore drops entries for plugins the loadout no longer provides and
    /// writes the result straight back to disk, the one change that cannot wait. It never adds or reorders
    /// anything; the write on the next sync does that.
    /// </remarks>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        var (owned, deployed) = PluginNames(syncTree);

        using var current = new MemoryStream();
        await stream.CopyToAsync(current);
        var existing = current.ToArray();

        var listed = OpenMwContentWriter.ContentNames(Decode(existing, out _));
        deployed.IntersectWith(listed);
        var pruned = Rewrite(existing, deployed, owned);
        if (pruned.AsSpan().SequenceEqual(existing)) return;

        var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        await onDisk.WriteAllBytesAsync(pruned);
    }

    private (HashSet<string> Owned, HashSet<string> Deployed) PluginNames(Dictionary<GamePath, SyncNode> syncTree)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, node) in syncTree)
        {
            if (path.Parent != game.DataPath || !PluginExtensions.Contains(path.Extension)) continue;
            owned.Add(path.FileName);
            // The vanilla masters are game files with a loadout part, so they count as deployed.
            if (node.HaveLoadout) deployed.Add(path.FileName);
        }
        return (owned, deployed);
    }

    // OpenMW writes the file as UTF-8. A byte order mark is kept if the file had one; bytes that are not valid
    // UTF-8 do not survive a rewrite.
    private static byte[] Rewrite(byte[] existing, HashSet<string> deployed, HashSet<string> owned)
    {
        var text = Decode(existing, out var hasBom);
        var rewritten = new UTF8Encoding(false).GetBytes(OpenMwContentWriter.Rewrite(text, deployed, owned));
        return hasBom ? [.. Utf8Bom, .. rewritten] : rewritten;
    }

    private static string Decode(byte[] bytes, out bool hasBom)
    {
        hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        return Encoding.UTF8.GetString(bytes.AsSpan(hasBom ? Utf8Bom.Length : 0));
    }
}
