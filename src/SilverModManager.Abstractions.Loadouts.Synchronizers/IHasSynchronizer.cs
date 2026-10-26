namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Something that owns the synchronizer for a game. <c>IGame</c> is declared in a project that depends on this
/// one, so code here that needs a game's synchronizer (the deployment chain asking for the game's path filter)
/// reaches it through this interface.
/// </summary>
public interface IHasSynchronizer
{
    /// <summary>
    /// The synchronizer for this game.
    /// </summary>
    ILoadoutSynchronizer Synchronizer { get; }
}
