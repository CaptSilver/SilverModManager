using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// The rule that keeps merged files out of a game folder that can go online: a game the merge engine serves and that
/// has <see cref="IGameData.HasOnlinePlay"/> deploys through an overlay, never into the real folder.
/// </summary>
public static class OverlayInvariant
{
    /// <summary>The games in <paramref name="games"/> that merge files, have online play and do not deploy through an overlay.</summary>
    public static IReadOnlyList<IGameData> Violations(IEnumerable<IGameData> games, MergeEngines engines)
        => games.Where(g => g.HasOnlinePlay && !g.DeploysThroughOverlay && engines.For(g) is not null).ToArray();

    /// <exception cref="InvalidOperationException">A game breaks the rule; the message names each one.</exception>
    public static void Check(IEnumerable<IGameData> games, MergeEngines engines)
    {
        var names = Violations(games, engines).Select(g => g.DisplayName).ToArray();
        if (names.Length == 0) return;

        throw new InvalidOperationException(
            $"{string.Join(", ", names)} merge{(names.Length == 1 ? "s" : "")} files and {(names.Length == 1 ? "has" : "have")} online play, so {(names.Length == 1 ? "it" : "they")} must deploy through an overlay and not write mods into the real game folder");
    }
}
