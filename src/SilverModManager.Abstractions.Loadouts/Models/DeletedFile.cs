using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Represents a deleted file.
/// </summary>
[PublicAPI]
[Include<LoadoutItemWithTargetPath>]
public partial class DeletedFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.DeletedFile";

    /// <summary>
    /// Reason.
    /// </summary>
    public static readonly StringAttribute Reason = new(Namespace, nameof(Reason));
}
