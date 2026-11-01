using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// The Nexus Mods files that open plans are waiting for the user to download in a browser, by owner. The nxm
/// handler asks this before it refuses a link.
/// </summary>
public sealed class ExpectedNexusDownloads : IExpectedNexusDownloads
{
    private readonly Lock _lock = new();
    private readonly Dictionary<object, Expectation> _byOwner = new();

    private sealed record Expectation(HashSet<(GameDomain, ModId, FileId)> Files, GameId? Game);

    /// <summary>
    /// Replaces what <paramref name="owner"/> expects. <paramref name="game"/> is the game the owner's plan is for, or
    /// null for a plan that has none.
    /// </summary>
    public void Set(object owner, IEnumerable<(GameDomain Domain, ModId ModId, FileId FileId)> files, GameId? game)
    {
        var set = files.ToHashSet();
        lock (_lock)
        {
            if (set.Count == 0) _byOwner.Remove(owner);
            else _byOwner[owner] = new Expectation(set, game);
        }
    }

    public void Clear(object owner)
    {
        lock (_lock) _byOwner.Remove(owner);
    }

    public bool IsExpected(GameDomain domain, ModId modId, FileId fileId)
    {
        lock (_lock) return _byOwner.Values.Any(expectation => expectation.Files.Contains((domain, modId, fileId)));
    }

    public bool TryGetExpectedGame(GameDomain domain, ModId modId, FileId fileId, out GameId game)
    {
        lock (_lock)
        {
            foreach (var expectation in _byOwner.Values)
            {
                if (expectation.Game is not { } owned || !expectation.Files.Contains((domain, modId, fileId))) continue;
                game = owned;
                return true;
            }
        }

        game = default;
        return false;
    }
}
