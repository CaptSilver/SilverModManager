using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.StardewValley.Models;

[Obsolete("Will be removed with a future migration")]
[Include<LoadoutItemGroup>]
public partial class SMAPIModLoadoutItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.StardewValley.SMAPIModLoadoutItem";

    public static readonly ReferenceAttribute<SMAPIManifestLoadoutFile> Manifest = new(Namespace, nameof(Manifest));
}
