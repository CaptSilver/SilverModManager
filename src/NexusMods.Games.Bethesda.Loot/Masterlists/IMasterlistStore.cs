namespace NexusMods.Games.Bethesda.Loot.Masterlists;

/// <summary>
/// The text of a masterlist and of the shared prelude it is loaded with.
/// </summary>
public sealed record MasterlistContent(string Masterlist, string Prelude);

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
