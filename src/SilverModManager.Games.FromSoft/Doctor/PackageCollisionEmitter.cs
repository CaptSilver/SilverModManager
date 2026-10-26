using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// Reports mods that provide the same file and the app will not merge. A file with no merge strategy is a Warning that
/// names the mod whose copy loadout priority keeps; one that is ambiguous or lacks a vanilla copy is Critical. Which
/// paths those are is the merge index's call, so this agrees with the apply: a path the engine merges is not a
/// collision, and one it can't merge says why.
/// Two files for one place in the overlay's config layer are reported separately: the later one would silently
/// replace the other there. Neither blocks the apply.
/// </summary>
public class PackageCollisionEmitter(LoadoutMergeSources sources) : IFromSoftDiagnosticEmitter
{
    private const string CaseNote = " The paths differ only in case, and the game's file lookup does not tell them apart.";

    /// <summary>
    /// The warnings for files that deploy whole from one package, whether the app has no merge for them or their merge
    /// failed: me3 mounts the last package it lists, which is the loadout's priority order, so the winner is known and
    /// the other copies are only dropped. Files with the same winner and the same ignored mods share one warning, since
    /// the player's choice is about the mods, not about each file.
    /// </summary>
    internal static IEnumerable<Diagnostic> WinnerWarnings(IEnumerable<MergeCandidate> candidates)
    {
        var groups = candidates
            .Select(candidate =>
            {
                var winner = candidate.Winner ?? throw new InvalidOperationException($"{candidate.Path} is not merged and has no winner");
                var ignored = candidate.Contributors.Select(c => c.ModName).Where(n => n != winner.ModName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return (Winner: winner.ModName, Ignored: ignored, Candidate: candidate);
            })
            .GroupBy(c => (c.Winner, Ignored: string.Join(", ", c.Ignored)));

        foreach (var group in groups)
        {
            var paths = group.Select(c => c.Candidate.Path).Order(StringComparer.Ordinal).ToArray();
            var reasons = group.Select(c => c.Candidate.Outcome.Reason ?? throw new InvalidOperationException($"{c.Candidate.Path} is not merged and says no reason"))
                .Distinct(StringComparer.Ordinal).ToArray();
            var ignored = group.First().Ignored;
            yield return FromSoftDiagnostics.CreatePackageWinnerByPriority(
                group.Key.Winner, group.Key.Ignored, string.Join(", ", paths), string.Join("; ", reasons),
                paths.Length == 1 ? paths[0] : $"{paths.Length} files", SwapSentence(group.Key.Winner, ignored));
        }
    }

    private const string Direction = "on Rules > File Conflicts (in the default order, the mod lower in the list wins).";

    private static string SwapSentence(string winner, IReadOnlyList<string> ignored) => ignored.Count == 1
        ? $"To use the copy from {ignored[0]} instead, move {ignored[0]} below {winner} {Direction}"
        : $"To use another mod's copy instead, move that mod ({string.Join(", ", ignored)}) below {winner} {Direction}";

    // The check reads the loadout's items, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var winners = new List<MergeCandidate>();
        foreach (var candidate in sources.Index(loadout).Candidates)
        {
            if (candidate.Outcome.Merges) continue;

            if (candidate.Winner is not null)
            {
                winners.Add(candidate);
                continue;
            }

            var packages = candidate.Contributors.Select(c => c.PackageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var caseOnly = candidate.Spellings.Count > 1;
            yield return FromSoftDiagnostics.CreatePackageCollision(
                string.Join(", ", packages),
                string.Join(", ", candidate.Spellings),
                caseOnly ? CaseNote : "",
                candidate.Outcome.Reason ?? throw new InvalidOperationException($"{candidate.Path} is not merged and says no reason"));
        }

        foreach (var warning in WinnerWarnings(winners)) yield return warning;

        var overlayFiles = new List<(string Mod, string Path)>();
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.GetThisAndParents().Any(p => p.IsDisabled)) continue;

            var target = (GamePath)item.TargetPath;
            if (target.LocationId != LocationId.Me3Mods) continue;

            var parts = target.Path.Path.Split('/', 2);
            if (parts.Length < 2) continue;

            if (parts[0].Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase))
                overlayFiles.Add((loadoutItem.Parent.AsLoadoutItem().Name, parts[1]));
        }

        foreach (var group in overlayFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mods = group.Select(f => f.Mod).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (group.Count() < 2) continue;

            yield return FromSoftDiagnostics.CreateOverlayCollision(string.Join(", ", mods), group.Key);
        }
    }
}
