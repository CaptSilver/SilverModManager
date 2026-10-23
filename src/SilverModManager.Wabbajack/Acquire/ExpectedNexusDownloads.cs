using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// The Nexus Mods files that open plans are waiting for the user to download in a browser, by owner. The nxm
/// handler asks this before it refuses a link.
/// </summary>
public sealed class ExpectedNexusDownloads : IExpectedNexusDownloads
{
    private readonly Lock _lock = new();
    private readonly Dictionary<object, HashSet<(GameDomain, ModId, FileId)>> _byOwner = new();

    /// <summary>Replaces what <paramref name="owner"/> expects.</summary>
    public void Set(object owner, IEnumerable<(GameDomain Domain, ModId ModId, FileId FileId)> files)
    {
        var set = files.ToHashSet();
        lock (_lock)
        {
            if (set.Count == 0) _byOwner.Remove(owner);
            else _byOwner[owner] = set;
        }
    }

    public void Clear(object owner)
    {
        lock (_lock) _byOwner.Remove(owner);
    }

    public bool IsExpected(GameDomain domain, ModId modId, FileId fileId)
    {
        lock (_lock) return _byOwner.Values.Any(set => set.Contains((domain, modId, fileId)));
    }
}
