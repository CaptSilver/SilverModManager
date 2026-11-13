using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.BaldursGate3;

internal static class ScriptExtenderFile
{
    /// <summary>
    /// The script extender's DLL in an enabled group of the loadout, if there is one.
    /// </summary>
    public static Optional<LoadoutFile.ReadOnly> Find(Loadout.ReadOnly loadout)
    {
        return LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup()
            .Where(g => g.AsLoadoutItem().IsEnabled())
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath()
                .OfTypeLoadoutFile()
                .Where(file => file.AsLoadoutItemWithTargetPath().TargetPath.Item2 == Bg3Constants.BG3SEGamePath.LocationId &&
                               file.AsLoadoutItemWithTargetPath().TargetPath.Item3 == Bg3Constants.BG3SEGamePath.Path
                )
            ).FirstOrOptional(_ => true);
    }
}
