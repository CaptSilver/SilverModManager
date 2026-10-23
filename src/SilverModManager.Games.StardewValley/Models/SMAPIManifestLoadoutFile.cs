using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.StardewValley.Models;

[Include<LoadoutFile>]
public partial class SMAPIManifestLoadoutFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.StardewValley.SMAPIManifestLoadoutFile";

    public static readonly MarkerAttribute ManifestFile = new(Namespace, nameof(ManifestFile)) { IsIndexed = true };

    public static IEnumerable<ReadOnly> GetAllInLoadout(IDb db, LoadoutId loadoutId, bool onlyEnabled)
    {
        var entityIds = db.Datoms(
            (ManifestFile, Null.Instance),
            (LoadoutItem.Loadout, loadoutId)
        );

        return entityIds
            .Select(entityId => Load(db, entityId))
            .Where(loadoutItem => loadoutItem.IsValid() && (!onlyEnabled || onlyEnabled && loadoutItem.AsLoadoutFile().AsLoadoutItemWithTargetPath().AsLoadoutItem().IsEnabled()));
    }
}
