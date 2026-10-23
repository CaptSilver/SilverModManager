using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Models;

[Include<LoadoutItemGroup>]
public partial class BannerlordModuleLoadoutItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.MountAndBlade2Bannerlord.ModLoadoutItem";

    public static readonly ReferenceAttribute<ModuleInfoFileLoadoutFile> ModuleInfo = new(Namespace, nameof(ModuleInfo));
}
