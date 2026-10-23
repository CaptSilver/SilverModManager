namespace SilverModManager.Games.Bethesda.Loot.Masterlists;

/// <summary>
/// The text of a masterlist and of the shared prelude it is loaded with.
/// </summary>
/// <param name="Revision">Says which copy of the masterlist this is: the bundled commit, or the date a refreshed copy was saved.</param>
public sealed record MasterlistContent(string Masterlist, string Prelude, string Revision);

/// <summary>
/// Where the current masterlist for a game comes from.
/// </summary>
public interface IMasterlistStore
{
    /// <summary>
    /// The newest masterlist and prelude available for <paramref name="game"/>. Does no network I/O.
    /// </summary>
    MasterlistContent Current(LootGame game);
}
