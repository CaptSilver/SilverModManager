using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Represents a group of game file loadout items.
/// </summary>
[PublicAPI]
[Include<LoadoutItemGroup>]
public partial class LoadoutGameFilesGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutGameFilesGroup";

    /// <summary>
    /// Game metadata.
    /// </summary>
    public static readonly ReferenceAttribute<Sdk.Games.GameInstallMetadata> GameMetadata = new(Namespace, nameof(GameMetadata));
}


