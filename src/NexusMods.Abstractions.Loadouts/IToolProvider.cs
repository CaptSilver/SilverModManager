using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Supplies tools that depend on the loadout, such as the programs a user added, alongside the tools that
/// are registered for a whole game.
/// </summary>
public interface IToolProvider
{
    /// <summary>
    /// The tools this provider has for the loadout.
    /// </summary>
    IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout);
}
