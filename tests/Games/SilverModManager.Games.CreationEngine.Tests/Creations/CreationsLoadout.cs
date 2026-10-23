using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Tests.Creations;

/// <summary>Reads back what a sync made of a game's Creations.</summary>
internal static class CreationsLoadout
{
    public static GameInstalledItem.ReadOnly[] Items(IDb db, LoadoutId loadout) =>
        GameInstalledGroup.FindByInstalledFor(db, loadout)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            .ToArray();

    /// <summary>The game-relative paths filed under External Changes.</summary>
    public static string[] ExternalChanges(IDb db, LoadoutId loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(db, loadout)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();
}
