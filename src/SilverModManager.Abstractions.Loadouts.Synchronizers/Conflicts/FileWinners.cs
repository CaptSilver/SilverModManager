using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Which of several files at one path the synchronizer deploys, read through the model accessors only (no database
/// query), so it is safe to call while the winning-files query runs. It mirrors the SQL macro
/// <c>synchronizer.WinnerRank</c> in <c>Synchronizer.sql</c>, which <c>WinningLeafLoadoutItem</c> takes the
/// <c>arg_max</c> of: a <see cref="LoadoutFileWinner"/> pin whose group is the file's direct parent ranks above every
/// priority; otherwise the direct parent's priority row counts, and a parent without one counts as zero. Only the
/// direct parent counts, not the mod above it. SQL <c>arg_max</c> is arbitrary on equal ranks, so a tie has no
/// synchronizer answer; callers break it themselves.
/// </summary>
public sealed class FileWinners
{
    private readonly IDb _db;
    private readonly Dictionary<(LocationId, RelativePath), LoadoutItemGroupId> _pins;
    private readonly Dictionary<LoadoutItemGroupId, ulong> _priorities = [];

    private FileWinners(IDb db, Dictionary<(LocationId, RelativePath), LoadoutItemGroupId> pins)
    {
        _db = db;
        _pins = pins;
    }

    public static FileWinners Of(IDb db, LoadoutId loadout)
    {
        var pins = new Dictionary<(LocationId, RelativePath), LoadoutItemGroupId>();
        foreach (var pin in LoadoutFileWinner.FindByLoadout(db, loadout))
            pins[(pin.TargetPath.Item2, pin.TargetPath.Item3)] = pin.WinnerGroupId;
        return new FileWinners(db, pins);
    }

    /// <summary>The rank of a file at <paramref name="path"/> whose direct parent is <paramref name="parent"/>; the higher rank is deployed.</summary>
    public UInt128 RankOf(GamePath path, LoadoutItemGroupId parent)
    {
        if (_pins.TryGetValue((path.LocationId, path.Path), out var pinned) && pinned == parent)
            return (UInt128)ulong.MaxValue + 1;

        if (!_priorities.TryGetValue(parent, out var priority))
            _priorities[parent] = priority = LoadoutItemGroupPriority.FindByTarget(_db, parent).Select(r => r.Priority.Value).DefaultIfEmpty(0UL).Max();
        return priority;
    }
}
