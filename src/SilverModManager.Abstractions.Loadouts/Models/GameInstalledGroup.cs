using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Everything the game installed into its own folder (Creations, in-game mods) that the app did not put
/// there. One per loadout, created when the first such item is recognised. Switching the group off
/// (<see cref="LoadoutItem.Disabled"/>) switches all of its items off at once.
/// </summary>
[Include<LoadoutItemGroup>]
public partial class GameInstalledGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameInstalledGroup";

    public static readonly ReferenceAttribute<Loadout> InstalledFor = new(Namespace, nameof(InstalledFor)) { IsIndexed = true };

    /// <summary>
    /// True when the entity carries content the game installed: a <see cref="GameInstalledFile.Hash"/> or a
    /// <see cref="GameInstalledItem.SourceId"/>. Its switch follows the group's, and it is not the user's to remove.
    /// </summary>
    public static bool IsInstalledContent<T>(T entity) where T : struct, IReadOnlyModel<T>
    {
        return GameInstalledItem.SourceId.TryGetValue(entity, out _) || GameInstalledFile.Hash.TryGetValue(entity, out _);
    }
}
