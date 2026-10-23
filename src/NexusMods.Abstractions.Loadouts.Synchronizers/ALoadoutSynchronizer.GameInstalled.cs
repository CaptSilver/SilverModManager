using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// Finds the content the game installed into its own folder. Null for games that have none.
    /// </summary>
    protected virtual IGameInstalledContentRecognizer? GameInstalledContent => null;

    /// <summary>
    /// Lets a game store its own data about a recognised item. Called inside the reconcile transaction for
    /// every item of the current parse.
    /// </summary>
    protected virtual void WriteGameInstalledItemExtras(ITransaction tx, EntityId itemId, GameInstalledContent content) { }
}
