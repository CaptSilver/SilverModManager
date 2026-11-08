using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Mapping;
using Placement = SilverModManager.Wabbajack.Install.InstallModlistJob.Placement;
using WriteState = SilverModManager.Wabbajack.Install.InstallModlistJob.WriteState;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// What an install decides by comparing the rows a group already holds with what the list asks for. The functions read a
/// <see cref="WriteState"/> and the list's placements and change nothing, so the install and a read-only look at an
/// install run the same code.
/// </summary>
internal static class InstallReconciler
{
    /// <summary>A folder whose files match this share of another's bytes is a renamed copy of it.</summary>
    public const double RenameThreshold = 0.5;

    /// <summary>A rename also has to share at least this many bytes, or <see cref="RenameFloorFiles"/> files; a tiny folder matches by accident.</summary>
    public const long RenameFloorBytes = 1 << 20;

    public const int RenameFloorFiles = 5;

    /// <summary>
    /// The directives of <paramref name="executable"/> whose file an earlier run already wrote. A directive is finished
    /// when the group holds a file at its path that the earlier run recorded for the same list hash, or, for a file
    /// written before the hash was recorded, a plain copy of a library file that has that hash. Each finished file is
    /// marked wanted so the stale-row pass keeps it. A directive with no file row (one that was skipped) is never
    /// finished, so a skip is always retried. A note an earlier run left that a file was kept as the original sends the
    /// directive through again, except when updating: there the note only matters if the row no longer matches the list.
    /// </summary>
    /// <param name="isGameIni">Whether a path is an INI the loadout's bake owns, which leaves no file row.</param>
    /// <param name="onFinished">Called with each finished directive and the identity of its file.</param>
    public static Placement[] FinishedDirectives(
        WriteState state,
        Placement[] executable,
        IReadOnlySet<string> keptOriginals,
        bool updateMode,
        Func<GamePath, bool> isGameIni,
        Func<Directive, FileIdentity, bool> isLibraryCopyOf,
        Action<Placement, FileIdentity> onFinished)
    {
        var finished = new List<Placement>();
        foreach (var placement in executable)
        {
            if (FindFinishedFile(state, placement, keptOriginals, updateMode, isGameIni, isLibraryCopyOf) is not { } found) continue;

            found.Known.Wanted = true;
            onFinished(placement, found.Identity);
            finished.Add(placement);
        }
        return finished.ToArray();
    }

    private static (FileIdentity Identity, KnownFile Known)? FindFinishedFile(
        WriteState state, Placement placement, IReadOnlySet<string> keptOriginals, bool updateMode, Func<GamePath, bool> isGameIni, Func<Directive, FileIdentity, bool> isLibraryCopyOf)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path) return null;
        if (!CanBeFinished(placement, path, isGameIni)) return null;
        if (!updateMode && keptOriginals.Contains(placement.To.ToString())) return null;
        if (!state.Files.TryGetValue(groupName, out var files)) return null;
        var expected = placement.Directive.XxHash64;

        foreach (var (identity, known) in files.AtPath(path))
        {
            if (known.ListHash is { } recorded ? recorded.Equals(expected) : isLibraryCopyOf(placement.Directive, identity)) return (identity, known);
        }
        return null;
    }

    /// <summary>
    /// Whether the directive's output is a file row whose bytes depend only on the list. A tool file and a game's INI
    /// baseline leave no such row, and a path-remapped file or a list-edited profile file depends on this machine.
    /// </summary>
    public static bool CanBeFinished(Placement placement, GamePath path, Func<GamePath, bool> isGameIni)
    {
        if (placement.Directive is RemappedInlineFile || InstallModlistJob.IsHashUnchecked(placement.Directive)) return false;
        return placement.Target switch
        {
            Mo2Target.ModFile or Mo2Target.GameRootFile => true,
            Mo2Target.ProfileSetting or Mo2Target.ProfileTweak => !isGameIni(path),
            _ => false,
        };
    }

    /// <summary>
    /// The rows of the list's settings group that are the user's own tweak of an INI: a row at a loadout-named tweak path
    /// that the list did not ask for and that is no longer what the list wrote. An earlier version installed the list's
    /// tweak under that name and the INI editor wrote the user's edits into the same group.
    /// </summary>
    /// <param name="isListOutput">Whether the row's bytes are still what the list wrote, judged from the row's recorded list hash and never from a library file.</param>
    public static IReadOnlyList<(GamePath Path, FileIdentity Identity, KnownFile Known)> UsersTweakRows(WriteState state, Func<GamePath, FileIdentity, bool> isListOutput)
    {
        var rows = new List<(GamePath, FileIdentity, KnownFile)>();
        if (!state.Files.TryGetValue(Mo2Layout.ProfileSettingsGroup, out var files)) return rows;

        foreach (var path in files.Paths)
        {
            if (!IniTweakLayers.IsLoadoutTweakPath(path, out _)) continue;
            foreach (var (identity, known) in files.AtPath(path))
            {
                if (known.Wanted || known.Ids.Count == 0 || isListOutput(path, identity)) continue;
                rows.Add((path, identity, known));
            }
        }
        return rows;
    }

    /// <summary>
    /// The ids of the rows to delete once the run is done, and the paths of the tweak rows kept as the user's. A row is
    /// stale when nothing asked for it: a file whose content changed has a row for the old hash next to the new one, a
    /// file the list dropped has none asking for it, and a wanted file's further rows are duplicates a past run left. That
    /// holds for the list's tweak rows too, whatever name they sit under: a tweak the list moved to another path, or one
    /// installed under an earlier list name, is stale. The one exception is a loadout-named tweak the list did not write
    /// (see <see cref="UsersTweakRows"/>).
    /// </summary>
    public static (IReadOnlyList<EntityId> Stale, IReadOnlyList<GamePath> UserTweaksKept) StaleRows(WriteState state, Func<GamePath, FileIdentity, bool> isListOutput)
    {
        var users = UsersTweakRows(state, isListOutput);
        var usersRows = users.Select(u => u.Known).ToHashSet();

        var stale = new List<EntityId>();
        foreach (var known in state.Files.Values.SelectMany(files => files.Known))
        {
            if (usersRows.Contains(known)) continue;
            stale.AddRange(known.Wanted ? known.Ids.Skip(1) : known.Ids);
        }
        return (stale, users.Select(u => u.Path).Distinct().ToArray());
    }

    /// <summary>The folders the list no longer has: groups the install made that no placement of the new list names. A folder emptied because this run skipped its files is still named, so it is not gone.</summary>
    public static IReadOnlySet<string> GoneFolders(WriteState state, IReadOnlyList<Placement> placements)
    {
        var named = NamedFolders(placements);
        return state.Files.Keys
            .Where(folder => !IsSettings(folder) && !named.Contains(folder))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pairs of (gone folder, new folder) where the new folder holds what the gone one did: the files the two share, by
    /// the list hash the old rows recorded, make up at least <see cref="RenameThreshold"/> of the old folder's bytes, and
    /// are at least <see cref="RenameFloorBytes"/> or <see cref="RenameFloorFiles"/> files. Each folder is in at most one
    /// pair, the closest match first.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> RenamedFolders(WriteState state, IReadOnlyList<Placement> placements, IReadOnlySet<string> goneFolders)
    {
        var added = NamedFolders(placements).Where(folder => !state.Files.ContainsKey(folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added.Count == 0 || goneFolders.Count == 0) return [];

        var foldersWith = new Dictionary<FileIdentity, HashSet<string>>();
        foreach (var placement in placements)
        {
            if (placement.Group is not { } folder || !added.Contains(folder)) continue;
            if (!foldersWith.TryGetValue(placement.Directive.XxHash64, out var set)) foldersWith[placement.Directive.XxHash64] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(folder);
        }

        var candidates = new List<(string From, string To, double Share, long Bytes)>();
        foreach (var gone in goneFolders.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!state.Files.TryGetValue(gone, out var files)) continue;

            long total = 0;
            var shared = new Dictionary<string, (long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);
            foreach (var known in files.Known)
            {
                var bytes = (long)known.Size.Value;
                total += bytes;
                if (known.ListHash is not { } hash || !foldersWith.TryGetValue(hash, out var inFolders)) continue;
                foreach (var folder in inFolders)
                {
                    var (soFar, count) = shared.GetValueOrDefault(folder);
                    shared[folder] = (soFar + bytes, count + 1);
                }
            }
            if (total == 0) continue;

            foreach (var (folder, (bytes, count)) in shared)
            {
                var share = (double)bytes / total;
                if (share >= RenameThreshold && (bytes >= RenameFloorBytes || count >= RenameFloorFiles))
                    candidates.Add((gone, folder, share, bytes));
            }
        }

        var pairs = new List<(string From, string To)>();
        var usedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Share)
                     .ThenByDescending(c => c.Bytes)
                     .ThenBy(c => c.From, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.To, StringComparer.OrdinalIgnoreCase))
        {
            if (usedFrom.Contains(candidate.From) || usedTo.Contains(candidate.To)) continue;
            usedFrom.Add(candidate.From);
            usedTo.Add(candidate.To);
            pairs.Add((candidate.From, candidate.To));
        }
        return pairs;
    }

    /// <summary>
    /// The game-folder files the list copies in that the group holds neither a row nor a skip note for: a run that
    /// finished leaves one or the other for every file it wrote or could not, so the user chose to keep the game's own.
    /// </summary>
    /// <param name="skippedDestinations">The list destinations of the skip notes the group held, spelled as the list does and compared without case.</param>
    public static IReadOnlySet<RelativePath> GameFilesKeptByUser(WriteState state, IReadOnlyList<Placement> placements, IReadOnlySet<string> skippedDestinations)
    {
        var kept = new HashSet<RelativePath>();
        foreach (var placement in placements)
        {
            if (placement.Target is not Mo2Target.GameRootFile root || placement.Group is not { } group || placement.Path is not { } path) continue;
            if (state.Files.TryGetValue(group, out var files) && files.AtPath(path).Count > 0) continue;
            if (skippedDestinations.Contains(placement.To.ToString())) continue;
            kept.Add(root.Path);
        }
        return kept;
    }

    /// <summary>The mod folders the list's placements name, not counting the profile settings group.</summary>
    public static HashSet<string> NamedFolders(IReadOnlyList<Placement> placements)
        => placements
            .Select(p => p.Group)
            .OfType<string>()
            .Where(folder => !IsSettings(folder))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsSettings(string folder) => folder.Equals(Mo2Layout.ProfileSettingsGroup, StringComparison.OrdinalIgnoreCase);
}
