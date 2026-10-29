using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// A game class implements this to be served by <see cref="MergeEngine"/>. That alone deploys no merged file: a game
/// makes the hookups below, and each one it forgets breaks something different.
/// <list type="number">
/// <item><description>
/// <b>This interface on the game</b>, returning its <see cref="AMergeGame"/>. Without it no engine serves the game: no
/// Merges page, no merge cards, and every contested file deploys by load order.
/// </description></item>
/// <item><description>
/// <b><see cref="MergeSynchronizerPart.IntrinsicFiles"/> added to the synchronizer's <c>IntrinsicFiles</c>.</b> Without
/// it the merged files are never in the sync tree, and <see cref="MergeSynchronizerPart.CheckOutputs"/> finds no listing
/// for the revision and refuses every apply as a loadout that changed.
/// </description></item>
/// <item><description>
/// <b><see cref="MergeSynchronizerPart.PlanOutputs"/> in <c>PreflightLoadout</c> and
/// <see cref="MergeSynchronizerPart.CheckOutputs"/> in <c>PreflightActions</c>.</b> Without the first, the listing does
/// not yet know a merge fails, so the first apply after it starts failing is refused once. Without the second, nothing
/// compares the tree with the plan and every merged file's write is refused, since no checked plan exists to copy.
/// </description></item>
/// <item><description>
/// <b><see cref="EngineMergeDiagnosticsEmitter"/> in the game's <c>DiagnosticEmitters</c>.</b> Without it Health Check
/// shows no merge cards, so every change a merge drops is dropped silently. <see cref="AMergeGame.CheckHookups"/> catches
/// this one; call it from the game's tests.
/// </description></item>
/// </list>
/// </summary>
public interface IHasMergeGame
{
    AMergeGame MergeGame { get; }
}

/// <summary>
/// What a game tells the engine: which files merge, where vanilla lives, where output goes, how a mod folder ranks.
/// <see cref="IHasMergeGame"/> lists what else the game has to hook up.
/// </summary>
public abstract class AMergeGame
{
    /// <summary>
    /// Throws when <paramref name="game"/> is served by the merge engine but its Health Check has no
    /// <see cref="EngineMergeDiagnosticsEmitter"/>, the one hookup the game object itself can show is missing. A game
    /// the engine does not serve passes.
    /// </summary>
    public static void CheckHookups(IGame game)
    {
        if (game is not IHasMergeGame) return;
        if (game.DiagnosticEmitters.OfType<EngineMergeDiagnosticsEmitter>().Any()) return;

        throw new InvalidOperationException(
            $"{game.DisplayName} implements {nameof(IHasMergeGame)} but has no {nameof(EngineMergeDiagnosticsEmitter)} in its {nameof(IGame.DiagnosticEmitters)}, so the changes its merges drop would raise no Health Check card");
    }

    /// <summary>Whether the game merges the contested file at <paramref name="contested"/> (the winner's path).</summary>
    public abstract bool Merges(GamePath contested);

    /// <summary>
    /// (Package, Rel) for a path under the mods location: Package is the top mod folder, Rel the path below it, in the
    /// contributor's spelling. Null when the path is not under a mod folder.
    /// </summary>
    public abstract (string Package, string Rel)? Split(GamePath path);

    /// <summary>How the game treats the path when more than one mod folder ships it. Contested unless a game says otherwise.</summary>
    public virtual SharedPath Sharing(GamePath path) => SharedPath.Contested;

    /// <summary>Where the game's own copy of <paramref name="rel"/> lives; null when the game ships none.</summary>
    public abstract GamePath? VanillaFor(string rel, GameInstallation installation);

    /// <summary>Where the merged file for <paramref name="rel"/> is written.</summary>
    public abstract GamePath OutputFor(string rel);

    public abstract IMergeStrategy StrategyFor(string rel);

    /// <summary>The bare name of the page where the user reorders mods, e.g. "Load Order". The report says "on the {ReorderHint} page".</summary>
    public abstract string ReorderHint { get; }

    /// <summary>
    /// The instruction that makes <paramref name="dropped"/> win over <paramref name="kept"/>. Every game's load order
    /// page lists the mod loaded last at the bottom, and that mod wins, so the instruction is the same for all of them.
    /// </summary>
    public static string ReorderAdvice(string dropped, string kept) => $"move {dropped} below {kept} so it loads after it";

    /// <summary>
    /// Whether the files of <paramref name="package"/> take part in merging. A game whose load order can switch a
    /// package off returns false for it, so a package the game does not load leaves nothing in the merged copy. Asked
    /// once per package and file each time the loadout is scanned, from inside the synchronizer's winning-files query,
    /// so an override has the same limits as <see cref="Rank"/>.
    /// </summary>
    public virtual bool Contributes(Loadout.ReadOnly loadout, string package) => true;

    /// <summary>
    /// Higher wins. Defaults to the greatest priority row of <paramref name="owner"/>, or the lowest rank when it has
    /// none. Asked once per mod folder and owner each time the loadout is scanned, from inside the synchronizer's
    /// winning-files query. Reading rows through model accessors is fine (the default's <c>FindByTarget</c> is one);
    /// an override must not run a SQL query, which would re-enter the running one, and must not read files.
    /// </summary>
    public virtual ulong Rank(Loadout.ReadOnly loadout, string package, LoadoutItemGroupId owner)
        => LoadoutItemGroupPriority.FindByTarget(loadout.Db, owner)
            .Select(r => r.Priority.Value)
            .DefaultIfEmpty(ConflictPriority.MinValue.Value)
            .Max();
}

/// <summary>How a game treats a path that more than one mod folder ships.</summary>
public enum SharedPath
{
    /// <summary>The game uses one copy: merged when <see cref="AMergeGame.Merges"/> says so, otherwise reported not mergeable.</summary>
    Contested,

    /// <summary>The game loads every folder's copy and what is inside them overlaps by priority: not merged, listed by <see cref="IMergeEngine.NotContested"/>.</summary>
    LoadedPerFolder,

    /// <summary>Each folder's copy is its own: nothing is merged, planned or reported.</summary>
    Independent,
}
