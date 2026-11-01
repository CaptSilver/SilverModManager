using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Types.IpcHandlers;

/// <summary>
/// An installation a link may install into. <paramref name="HasAppliedLoadout"/> is set when the app has applied a
/// loadout to it, which is how a link that fits several games picks the one in use.
/// </summary>
public record ManagedGame(GameInstallation Installation, bool HasAppliedLoadout);

/// <summary>The loadout a link installs into.</summary>
public readonly record struct ManagedLoadout(LoadoutId Id, string Name);

/// <summary>
/// The installations the app manages, as the protocol handlers see them. A link names a game by some key of its own
/// (a Nexus Mods domain, a Thunderstore community), so each handler passes the test for its key.
/// </summary>
public class ManagedGames(IGameRegistry registry, IConnection connection, ISynchronizerService synchronizer)
{
    /// <summary>
    /// The installations <paramref name="isGame"/> accepts whose game has a loadout or an applied loadout, in the
    /// registry's order. A loadout counts for every installation of its game, which is how Nexus links have always
    /// found their game.
    /// </summary>
    public virtual IReadOnlyList<ManagedGame> Candidates(Func<IGameData, bool> isGame)
        => Select(isGame, installation => Loadout.All(connection.Db).Any(loadout => IsSameGame(loadout.Game, installation.Game)));

    /// <summary>
    /// The installations <paramref name="isGame"/> accepts that have an applied loadout or a loadout of their own,
    /// in the registry's order. A loadout belongs to one installation, so a second install of the same game does
    /// not count through the first one's loadouts.
    /// </summary>
    public virtual IReadOnlyList<ManagedGame> InstallationCandidates(Func<IGameData, bool> isGame)
        => Select(isGame, installation => LoadoutsOf(installation).Any());

    /// <summary>
    /// The loadout a link installs into: the installation's last applied one, else the first one that belongs to
    /// it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The installation has no loadout.</exception>
    public virtual ManagedLoadout LoadoutOf(GameInstallation installation)
    {
        if (synchronizer.TryGetLastAppliedLoadout(installation, out var applied)) return new ManagedLoadout(applied.LoadoutId, applied.Name);

        var first = LoadoutsOf(installation).FirstOrDefault();
        if (first.IsValid() is false) throw new InvalidOperationException($"{installation.Game.DisplayName} has no loadout.");
        return new ManagedLoadout(first.LoadoutId, first.Name);
    }

    private List<ManagedGame> Select(Func<IGameData, bool> isGame, Func<GameInstallation, bool> hasLoadouts)
    {
        var candidates = new List<ManagedGame>();
        foreach (var installation in registry.LocateGameInstallations())
        {
            if (isGame(installation.Game) is false) continue;

            var applied = synchronizer.TryGetLastAppliedLoadout(installation, out _);
            if (applied || hasLoadouts(installation)) candidates.Add(new ManagedGame(installation, applied));
        }

        return candidates;
    }

    private IEnumerable<Loadout.ReadOnly> LoadoutsOf(GameInstallation installation)
        => Loadout.All(connection.Db).Where(loadout => registry.TryGetGameInstallation(loadout, out var owner) && owner == installation);

    // Games with a Nexus Mods id are the same game when the ids match, as they always were; the rest, which would all
    // read as "no id", are told apart by their own id.
    private static bool IsSameGame(IGameData a, IGameData b)
        => a.NexusModsGameId.HasValue && b.NexusModsGameId.HasValue
            ? a.NexusModsGameId.Value == b.NexusModsGameId.Value
            : a.GameId == b.GameId;
}
