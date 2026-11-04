using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>The one place that decides which sync nodes are unwritable files.</summary>
public static class UnwritableFiles
{
    /// <summary>Whether a mapped node is one an Apply cannot write.</summary>
    public static bool Is(SyncNode node) => node.Actions.HasFlag(Actions.WarnOfUnableToExtract);

    /// <summary>Why the node cannot be written. Meaningful only when <see cref="Is"/> says it is unwritable.</summary>
    public static UnwritableReason ReasonOf(SyncNode node) => node.HaveDisk ? UnwritableReason.DifferentOnDisk : UnwritableReason.NotOnDisk;

    /// <summary>Every unwritable file in a tree whose actions are already mapped, in path order.</summary>
    public static IReadOnlyList<UnwritableFile> Of(IEnumerable<KeyValuePair<GamePath, SyncNode>> tree) => tree
        .Where(kv => Is(kv.Value))
        .Select(kv => new UnwritableFile(kv.Key, ReasonOf(kv.Value)))
        .OrderBy(f => f.Path.Path.ToString(), StringComparer.Ordinal)
        .ToArray();
}
