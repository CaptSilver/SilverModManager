using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine;

internal static class ScriptExtenderFile
{
    /// <summary>
    /// The script extender's DLL at <paramref name="dllPath"/> in an enabled group of the loadout, if there is one.
    /// </summary>
    public static Optional<LoadoutFile.ReadOnly> Find(Loadout.ReadOnly loadout, GamePath dllPath)
    {
        return LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup()
            .Where(g => g.AsLoadoutItem().IsEnabled())
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath()
                .OfTypeLoadoutFile()
                .Where(file => file.AsLoadoutItemWithTargetPath().TargetPath.Item2 == dllPath.LocationId &&
                               file.AsLoadoutItemWithTargetPath().TargetPath.Item3 == dllPath.Path
                )
            ).FirstOrOptional(_ => true);
    }
}
