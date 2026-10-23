using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.StardewValley.Models;

[Include<LoadoutFile>]
public partial class SMAPIModDatabaseLoadoutFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.StardewValley.SMAPIModDatabaseLoadoutFile";

    public static readonly MarkerAttribute ModDatabaseFile = new(Namespace, nameof(ModDatabaseFile));
}
