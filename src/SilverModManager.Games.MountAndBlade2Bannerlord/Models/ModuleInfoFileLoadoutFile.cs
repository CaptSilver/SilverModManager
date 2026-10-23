using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Models;

[Include<LoadoutFile>]
public partial class ModuleInfoFileLoadoutFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.MountAndBlade2Bannerlord.ModuleInfoLoadoutFile";

    public static readonly MarkerAttribute ModuleInfoFile = new(Namespace, nameof(ModuleInfoFile));
}
